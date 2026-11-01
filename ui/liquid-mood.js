'use strict';

/* Liquid Mood — короткий вопрос о настроении и мягкая подборка.
   Картинки берутся ТОЛЬКО из локальных папок (cats / dogs / memes) и запрашиваются только после явного выбора категории:
   единственная точка входа — openCategory(), её вызывает кнопка категории. Картинки показываются по одной.
   Связь с приложением прежняя: get_liquid_mood_images(category) → {ok, images:[{name,url}], folder, error},
   set_liquid_mood(enabled) → {ok, error}, configuration.liquid_mood = {enabled}. */
window.LiquidMood = (() => {
  const byId = id => document.getElementById(id);
  const shell = document.querySelector('.shell');
  const panel = byId('liquid-mood');
  if (!shell || !panel) return {configure() {}, show() {}, isOpen: () => false, dismiss() {}};

  const stage = byId('mood-stage'), title = byId('liquid-mood-title'), copy = byId('mood-copy');
  const actions = byId('mood-actions'), status = byId('mood-status'), back = byId('mood-back');
  const frame = byId('mood-frame'), image = byId('mood-image'), counter = byId('mood-counter'), viewer = byId('mood-viewer');
  const toggle = byId('liquid-mood-enabled'), showNow = byId('liquid-mood-show');

  /* ---------- иконки: линейные SVG, без эмодзи ---------- */
  const ICONS = {
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M18.4 5.6 17 7M7 17l-1.4 1.4"/>',
    cloud: '<path d="M7 18h9.5a4 4 0 0 0 .6-7.95A5.5 5.5 0 0 0 6.6 9.2 4.4 4.4 0 0 0 7 18z"/>',
    moon: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>',
    headphones: '<path d="M4 14v-2a8 8 0 0 1 16 0v2"/><rect x="3" y="14" width="4" height="6" rx="1.6"/><rect x="17" y="14" width="4" height="6" rx="1.6"/>',
    note: '<path d="M9 18V6l10-2v12"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="16.5" cy="16" r="2.5"/>',
    cat: '<path d="M5 9 4 4l4.5 2.5a8 8 0 0 1 7 0L20 4l-1 5c.7 1.2 1 2.4 1 3.7 0 4-3.6 6.8-8 6.8s-8-2.8-8-6.8c0-1.3.3-2.5 1-3.7z"/><path d="M9.5 12.5h.01M14.5 12.5h.01M10.5 15.5c.9.7 2.1.7 3 0"/>',
    dog: '<path d="M8 6 5.5 5C4 6 3 9 3.5 12c.3 1.6 1.3 2.4 2.5 2.4M16 6l2.5-1C20 6 21 9 20.5 12c-.3 1.6-1.3 2.4-2.5 2.4"/><path d="M8 6h8c1 1.6 1.5 3.4 1.5 5.5 0 4-2.3 7-5.5 7s-5.5-3-5.5-7C6.5 9.4 7 7.6 8 6z"/><path d="M10 11h.01M14 11h.01M10.8 15c.8.6 1.6.6 2.4 0"/>',
    laugh: '<circle cx="12" cy="12" r="9"/><path d="M8.5 10h.01M15.5 10h.01"/><path d="M7.5 13.5c.8 3 3 4.5 4.5 4.5s3.7-1.5 4.5-4.5z"/>',
    next: '<path d="M5 12h14M13 6l6 6-6 6"/>',
    refresh: '<path d="M20 11a8 8 0 1 0-2.3 5.6M20 4v7h-7"/>',
    play: '<path d="M8 5.5v13l11-6.5z"/>'
  };
  const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true">${ICONS[name] || ''}</svg>`;

  /* ---------- тексты ---------- */
  const CATEGORIES = {
    cats: {label: 'Фото котов', alt: 'Фото кота из вашей коллекции', icon: 'cat'},
    dogs: {label: 'Фото собак', alt: 'Фото собаки из вашей коллекции', icon: 'dog'},
    memes: {label: 'Смешные мемы', alt: 'Мем из вашей коллекции', icon: 'laugh'}
  };
  const SCREENS = {
    prompt: {
      title: 'Как настроение сегодня?', copy: 'Небольшая пауза — только для вас.', layout: 'tiles',
      items: [
        {id: 'good', label: 'Всё отлично', icon: 'sun'},
        {id: 'low', label: 'Не лучший день', icon: 'cloud'},
        {id: 'tired', label: 'Чувствую усталость', icon: 'moon'},
        {id: 'music', label: 'Просто слушаю музыку', icon: 'headphones'},
        {id: 'dismiss', label: 'Не сейчас', kind: 'quiet'}
      ]
    },
    support: {
      title: 'Чем вас порадовать?', copy: 'Выберите что-то одно или просто оставьте музыку.', layout: 'tiles', back: 'prompt',
      items: [
        {id: 'cats', label: CATEGORIES.cats.label, icon: 'cat'},
        {id: 'dogs', label: CATEGORIES.dogs.label, icon: 'dog'},
        {id: 'memes', label: CATEGORIES.memes.label, icon: 'laugh'},
        {id: 'calm', label: 'Лучше музыку', icon: 'note'},
        {id: 'dismiss', label: 'Не сейчас', kind: 'quiet'}
      ]
    },
    good: {title: 'Рады это слышать.', copy: 'Пусть музыка сделает день ещё лучше.', layout: 'bar', items: [{id: 'dismiss', label: 'Вернуться к музыке', kind: 'primary'}]},
    music: {title: 'Пусть музыка задаёт темп.', copy: 'Самое время для треков, которые вы любите.', layout: 'bar', items: [{id: 'dismiss', label: 'Вернуться к музыке', kind: 'primary'}]},
    tired: {
      title: 'Не спешите.', copy: 'Спокойная музыка поможет перевести дух.', layout: 'bar',
      items: [{id: 'calm', label: 'Включить спокойную музыку', icon: 'note', kind: 'primary'}, {id: 'dismiss', label: 'Не сейчас', kind: 'quiet'}]
    },
    calmed: {title: 'Спокойная музыка уже в пути.', copy: 'Не спешите — плеер остался на своём месте.', layout: 'bar', items: [{id: 'dismiss', label: 'Вернуться к музыке', kind: 'primary'}]}
  };

  /* ---------- состояние ---------- */
  let enabled = false, presented = false, dismissed = false, busy = false;
  let view = 'prompt', category = null, version = 0;
  let pool = [], queue = [], lastItem = null, cycle = 0, shown = 0, loadingImage = false;

  const call = async (method, ...args) => {
    try { return typeof invoke === 'function' ? (await invoke(method, ...args)) || {ok: false} : {ok: false, error: 'Нет связи с приложением.'}; }
    catch (error) { return {ok: false, error: error && error.message ? error.message : 'Не удалось выполнить действие.'}; }
  };
  const notify = message => { if (message && typeof toast === 'function') toast(message); };

  /* ---------- отрисовка ---------- */
  function setStatus(content) {
    status.replaceChildren();
    if (content instanceof Node) status.append(content); else if (content) status.textContent = content;
    status.hidden = !content;
  }

  function button(item) {
    const el = document.createElement('button');
    el.type = 'button'; el.dataset.mood = item.id;
    if (item.kind) { el.className = 'mood-btn ' + item.kind; if (item.icon) el.insertAdjacentHTML('beforeend', icon(item.icon)); el.append(item.label); }
    else {
      el.className = 'mood-tile';
      el.insertAdjacentHTML('beforeend', `<span class="mood-icon">${icon(item.icon)}</span>`);
      const label = document.createElement('span'); label.className = 'mood-label'; label.textContent = item.label; el.append(label);
    }
    return el;
  }

  function render(name, overrides = {}) {
    const screen = {...SCREENS[name], ...overrides};
    const hadFocus = panel.contains(document.activeElement);
    view = name; version++; loadingImage = false; busy = false;
    panel.dataset.view = name === 'viewer' ? 'viewer' : (screen.layout === 'tiles' ? 'choice' : 'reply');
    title.textContent = screen.title; copy.textContent = screen.copy || '';
    actions.dataset.layout = screen.layout;
    actions.replaceChildren(...screen.items.map(button));
    back.hidden = !screen.back; back.dataset.target = screen.back || '';
    viewer.hidden = name !== 'viewer';
    setStatus(screen.status || '');
    stage.classList.remove('swap'); void stage.offsetWidth; stage.classList.add('swap');   // мягкая смена экрана
    panel.hidden = false; shell.classList.add('mood-open');
    if (hadFocus) actions.querySelector('button')?.focus({preventScroll: true});
  }

  function hide(markDismissed = true) {
    if (markDismissed) dismissed = true;
    version++; pool = []; queue = []; lastItem = null; image.removeAttribute('src');
    panel.hidden = true; shell.classList.remove('mood-open'); delete panel.dataset.view;
  }

  /* ---------- картинки: по одной ---------- */
  const shuffle = list => { for (let i = list.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [list[i], list[j]] = [list[j], list[i]]; } return list; };

  function refill() {
    queue = shuffle(pool.slice());
    if (queue.length > 1 && queue[0] === lastItem) queue.push(queue.shift());   // одна и та же картинка подряд не повторяется
    cycle = queue.length; shown = 0;
  }

  const preload = url => new Promise(resolve => {
    const probe = new Image(); const timer = setTimeout(() => resolve(false), 15000);
    probe.decoding = 'async';
    probe.onload = () => { clearTimeout(timer); resolve(true); };
    probe.onerror = () => { clearTimeout(timer); resolve(false); };
    probe.src = url;
  });

  function folderNote(prefix, folder, suffix) {
    const wrap = document.createDocumentFragment();
    wrap.append(prefix);
    if (folder) { const path = document.createElement('span'); path.className = 'mood-path'; path.textContent = folder; wrap.append(path); }
    wrap.append(suffix);
    return wrap;
  }

  /** Пустая папка / ошибка: сообщение вместо картинки, кнопка повторной проверки. */
  function viewerProblem(message) {
    render('viewer', {
      title: CATEGORIES[category].label, copy: '', layout: 'bar', back: 'support',
      items: [{id: 'refresh', label: 'Проверить снова', icon: 'refresh', kind: 'primary'}, {id: 'dismiss', label: 'Закрыть', kind: 'quiet'}],
      status: message
    });
    viewer.hidden = true;
    panel.dataset.view = 'reply';           // сообщение по центру, как и остальные ответы
  }

  async function openCategory(name) {
    const meta = CATEGORIES[name];
    if (!meta || busy) return;
    category = name; pool = []; queue = []; lastItem = null;
    render('viewer', {title: meta.label, copy: '', layout: 'bar', back: 'support',
      items: [{id: 'next', label: 'Ещё', icon: 'next', kind: 'primary'}, {id: 'dismiss', label: 'Закрыть', kind: 'quiet'}]});
    viewer.dataset.state = 'loading'; counter.textContent = ''; image.removeAttribute('src');
    actions.querySelector('[data-mood="next"]').disabled = true;
    const mine = version; busy = true;

    const result = await call('get_liquid_mood_images', name);
    if (mine !== version) return;
    busy = false;
    if (!result.ok) return viewerProblem(result.error || 'Не удалось открыть локальную папку.');
    pool = (Array.isArray(result.images) ? result.images : []).filter(item => item && typeof item.url === 'string' && item.url);
    if (!pool.length) {
      return viewerProblem(folderNote('В этой папке пока нет изображений. Добавьте файлы в ', result.folder, ' и нажмите «Проверить снова».'));
    }
    showNext();
  }

  async function showNext() {
    if (loadingImage || !pool.length) return;
    loadingImage = true; viewer.dataset.state = 'loading';
    const next = actions.querySelector('[data-mood="next"]'); if (next) next.disabled = true;
    const mine = version;
    while (pool.length) {
      if (!queue.length) refill();
      const item = queue.shift();
      if (await preload(item.url)) {
        if (mine !== version) return;
        shown++; lastItem = item;
        image.alt = CATEGORIES[category].alt;
        image.src = item.url;
        frame.style.setProperty('--mood-img', `url(${JSON.stringify(item.url)})`);   // размытый фон из той же картинки
        counter.textContent = `${shown} из ${cycle}`;
        viewer.dataset.state = 'ready';
        image.classList.remove('enter'); void image.offsetWidth; image.classList.add('enter');
        loadingImage = false;
        if (next) next.disabled = pool.length < 2;
        if (queue[0]) { const warm = new Image(); warm.src = queue[0].url; }      // следующая уже прогрета: «Ещё» срабатывает мгновенно
        return;
      }
      if (mine !== version) return;
      pool = pool.filter(candidate => candidate !== item);                           // битый файл исключаем
      queue = queue.filter(candidate => candidate !== item);
    }
    loadingImage = false;
    viewerProblem('Не удалось открыть ни одного изображения. Проверьте файлы в папке и нажмите «Проверить снова».');
  }

  async function playCalmMusic() {
    if (busy) return;
    busy = true;
    const result = typeof command === 'function' ? await command('genre', 'Релакс') : {ok: false};
    busy = false;
    if (result && result.ok) render('calmed');
  }

  /* ---------- события ---------- */
  actions.addEventListener('click', event => {
    const target = event.target.closest('button[data-mood]');
    if (!target || target.disabled) return;
    switch (target.dataset.mood) {
      case 'good': render('good'); break;
      case 'low': render('support'); break;
      case 'tired': render('tired'); break;
      case 'music': render('music'); break;
      case 'cats': case 'dogs': case 'memes': openCategory(target.dataset.mood); break;
      case 'calm': playCalmMusic(); break;
      case 'next': showNext(); break;
      case 'refresh': if (category) openCategory(category); break;
      case 'dismiss': hide(); break;
    }
  });
  frame.addEventListener('click', () => { if (view === 'viewer' && pool.length > 1) showNext(); });
  back.addEventListener('click', () => { if (back.dataset.target && SCREENS[back.dataset.target]) render(back.dataset.target); });

  toggle.addEventListener('change', async () => {
    const requested = toggle.checked;
    toggle.disabled = true;
    const result = await call('set_liquid_mood', requested);
    toggle.disabled = false;
    if (!result.ok) { toggle.checked = !requested; notify(result.error); return; }
    enabled = requested; showNow.disabled = !enabled;
    if (typeof configuration !== 'undefined' && configuration) configuration.liquid_mood = {enabled};
    if (enabled) { dismissed = false; presented = false; render('prompt'); presented = true; }
    else hide(false);
  });
  showNow.addEventListener('click', () => {
    byId('settings-dialog')?.close?.();            // настройки — модальное окно: закрываем, чтобы плашка была видна
    dismissed = false; presented = true; render('prompt');
  });

  function configure(options) {
    enabled = options?.enabled === true;
    toggle.checked = enabled; showNow.disabled = !enabled;
    if (!enabled) hide(false);
    else if (!presented && !dismissed) { presented = true; render('prompt'); }
  }

  const sync = () => { if (typeof configuration !== 'undefined' && configuration) configure(configuration.liquid_mood); };
  window.addEventListener('liquidready', sync);
  sync();

  return {configure, show: () => { if (enabled) { dismissed = false; presented = true; render('prompt'); } }, isOpen: () => !panel.hidden, dismiss: () => hide()};
})();
