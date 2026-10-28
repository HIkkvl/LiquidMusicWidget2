'use strict';
const $ = id => document.getElementById(id);
const clone = value => JSON.parse(JSON.stringify(value));
const defaultBindings = Object.fromEntries([['toggle','Num0'],['next','Num6'],['previous','Num4'],['play_pause','Num5'],['stop','Num2'],['like','Num1'],['voice','Num3']].map(([action,key]) => [action,{key,ctrl:false,alt:false,shift:false}]));
const fallbackGenres = ['Хип-хоп','Рок','Электроника','Поп','Инди','Метал','Альтернатива','Джаз','Релакс','Лоу-фай','R&B','Классика','Техно','Ретро'];
let configuration = null;
let currentState = {};
let editing = {};
let editingOverlay = {};
const defaultOverlay = {enabled:true,corner:"top-right",duration:4};
const defaultWheel = {enabled:true, modifier:'ctrl', step:5};
let editingWheel = {};
let busyPoll = false;
let toastTimer;
let lastArtwork = null;
let api = null;
let volumeDragging = false;
const shortcutLabel = b => [...['ctrl','alt','shift'].filter(k=>b[k]).map(k=>k[0].toUpperCase()+k.slice(1)),b.key || 'Не назначена'].join(' + ');
const icon = (element,name) => element.querySelector('use').setAttribute('href','#i-'+name);

function toast(message) {
  $('toast').textContent = message;
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(()=>$('toast').hidden=true,4200);
}
async function invoke(method,...args) {
  if (!api) return {ok:false,error:'Запустите виджет через START_WIDGET.bat'};
  try { return await api[method](...args); }
  catch(error) { return {ok:false,error:'Не удалось выполнить действие: '+error.message}; }
}
async function command(action,value) {
  const result = await invoke('command',action,value ?? null);
  if (!result.ok) toast(result.error);
  return result;
}
function renderGenres(genres) {
  $('genres').replaceChildren();
  genres.forEach((genre,index)=>{
    const row=document.createElement('button');
    row.className='genre';
    row.title='Найти плейлисты: '+genre;
    row.setAttribute('aria-label','Найти плейлисты жанра '+genre);
    const img=document.createElement('img');
    img.src='assets/genre-'+index+'.svg';
    img.alt='';
    const text=document.createElement('span');
    text.className='genre-name';text.textContent=genre;
    const arrow=document.createElementNS('http://www.w3.org/2000/svg','svg');
    const use=document.createElementNS('http://www.w3.org/2000/svg','use');
    use.setAttribute('href','#i-chevron');arrow.append(use);
    row.append(img,text,arrow);
    row.addEventListener('click',async()=>{
      const result=await command('genre',genre);
      if(result.ok) {
        document.querySelectorAll('.genre').forEach(el=>el.classList.remove('selected'));
        row.classList.add('selected');
        toast('Поиск подборок «'+genre+'» в Яндекс Музыке');
      }
    });
    $('genres').append(row);
  });
}
function updateShortcuts() {
  if(!configuration) return;
  $('open-shortcut').textContent=shortcutLabel(configuration.bindings.toggle);
  const titles={next:'Следующий трек',previous:'Предыдущий трек',play_pause:'Пауза / продолжить',stop:'Стоп'};
  document.querySelectorAll('[data-command]').forEach(el=>{
    const action=el.dataset.command;
    el.title=titles[action]+' · '+shortcutLabel(configuration.bindings[action]);
  });
  $('like-button').title='Лайк / убрать лайк · '+shortcutLabel(configuration.bindings.like);
  $('voice-button').title='Голосовая команда · '+shortcutLabel(configuration.bindings.voice || {key:''});
}
function showSettings() {
  if(!configuration){toast('Подождите, настройки загружаются');return;}
  editing=clone(configuration.bindings);
  editingOverlay=clone(configuration.overlay || defaultOverlay);
  editingWheel=clone(configuration.wheel || defaultWheel);
  renderShortcutRows();
  wireWheelSettings();
  $('settings-error').hidden=true;
  if(configuration.hotkey_error) showSettingsError(configuration.hotkey_error);
  $('settings-dialog').showModal();
}
function showSettingsError(message) {
  $('settings-error').textContent=message;
  $('settings-error').hidden=false;
}
function renderShortcutRows() {
  $('shortcut-rows').replaceChildren();
  if(configuration.backend==='csharp'){renderBrowserSettings();renderAssistantSettings();}
  renderOverlaySettings();
  Object.entries(configuration.actions).forEach(([action,label])=>{
    const row=document.createElement('div');row.className='shortcut-row';
    const name=document.createElement('label');name.className='shortcut-label';name.textContent=label;name.htmlFor='key-'+action;
    const select=document.createElement('select');select.id='key-'+action;
    const keys=action==='toggle'?configuration.keys:['',...configuration.keys];
    keys.forEach(key=>{const option=document.createElement('option');option.value=key;option.textContent=key||'Не назначена';select.append(option);});
    select.value=editing[action].key;
    select.addEventListener('change',()=>editing[action].key=select.value);
    const modifiers=document.createElement('div');modifiers.className='modifiers';
    ['ctrl','alt','shift'].forEach(mod=>{
      const label=document.createElement('label');label.className='modifier';
      const input=document.createElement('input');input.type='checkbox';input.checked=editing[action][mod];input.setAttribute('aria-label',mod+' для '+name.textContent);
      input.addEventListener('change',()=>editing[action][mod]=input.checked);
      const span=document.createElement('span');span.textContent=mod[0].toUpperCase()+mod.slice(1);
      label.append(input,span);modifiers.append(label);
    });
    row.append(name,select,modifiers);$('shortcut-rows').append(row);
  });
}
let browserSetupPending=false;
function renderAssistantSettings() {
  const section=document.createElement('section');section.className='overlay-options';
  const title=document.createElement('h3');title.textContent='Умный помощник · DeepSeek';
  const note=document.createElement('p');note.textContent='Понимает свободные фразы и уточнения, учитывает текущий трек. Текст сложных запросов, данные плеера и короткая история отправляются в DeepSeek; API платный. Голос распознаётся локально. Простые команды работают без интернета. Ответы показаны текстом.';
  const enabledLabel=document.createElement('label');enabledLabel.className='overlay-enabled';
  const enabled=document.createElement('input');enabled.type='checkbox';enabled.id='deepseek-enabled';enabled.checked=!!configuration.deepseek?.enabled;
  enabledLabel.append(enabled,document.createTextNode('Использовать DeepSeek для свободных фраз'));
  const key=document.createElement('input');key.type='password';key.id='deepseek-key';key.autocomplete='off';key.setAttribute('aria-label','API-ключ DeepSeek');key.placeholder=configuration.deepseek?.configured?'Ключ сохранён — введите новый для замены':'API-ключ DeepSeek';
  const status=document.createElement('p');status.id='deepseek-status';status.setAttribute('role','status');
  const save=document.createElement('button');save.type='button';save.id='save-deepseek';save.textContent='Сохранить помощника';
  const test=document.createElement('button');test.type='button';test.id='test-deepseek';test.textContent='Проверить подключение';
  const remove=document.createElement('button');remove.type='button';remove.id='delete-deepseek';remove.textContent='Удалить ключ';
  function paint(){const s=configuration.deepseek||{};enabled.checked=!!s.enabled;status.textContent=s.configured?(s.enabled?'Включён · ':'Ключ сохранён · ')+(s.model||'DeepSeek'):'Ключ не настроен';test.disabled=remove.disabled=!s.configured;}
  async function update(method,...args){
    save.disabled=test.disabled=remove.disabled=enabled.disabled=true;
    try {
      const result=await invoke(method,...args);
      if(result.deepseek)configuration.deepseek=result.deepseek;
      if(result.ok){key.value='';key.placeholder=configuration.deepseek?.configured?'Ключ сохранён — введите новый для замены':'API-ключ DeepSeek';paint();if(method==='test_deepseek')status.textContent='Подключение проверено · '+configuration.deepseek.model;}
      else {paint();showSettingsError(result.error);}
    } finally {save.disabled=enabled.disabled=false;test.disabled=remove.disabled=!configuration.deepseek?.configured;}
  }
  save.addEventListener('click',()=>update('save_deepseek',key.value,enabled.checked));
  test.addEventListener('click',()=>update('test_deepseek'));
  remove.addEventListener('click',()=>update('delete_deepseek'));
  section.append(title,note,enabledLabel,key,status,save,test,remove);$('shortcut-rows').append(section);paint();
}
function renderBrowserSettings() {
  const section=document.createElement('section');section.className='overlay-options';
  const title=document.createElement('h3');title.id='browser-name';
  const state=document.createElement('p');state.id='browser-status';state.setAttribute('role','status');state.setAttribute('aria-live','polite');
  const note=document.createElement('p');note.textContent='Используется браузер по умолчанию Windows. Подключите его один раз: настройка сохраняется и восстанавливается при запуске приложения.';
  const persist=document.createElement('label');persist.className='overlay-enabled';
  const checkbox=document.createElement('input');checkbox.type='checkbox';checkbox.id='browser-persist-shortcuts';checkbox.checked=true;
  persist.append(checkbox,document.createTextNode('Закрепить подключение в ярлыках браузера (с резервными копиями)'));
  const button=document.createElement('button');button.type='button';button.id='connect-browser';
  button.addEventListener('click',async()=>{
    browserSetupPending=true;renderBrowserConnection(configuration.browser);
    const result=await invoke('setup_browser',checkbox.checked);
    browserSetupPending=false;
    if(result.browser)configuration.browser=result.browser;
    renderBrowserConnection(configuration.browser);
    if(!result.ok&&!result.cancelled)showSettingsError(result.error);
    else if(result.ok&&result.browser?.connected){$('settings-error').hidden=true;toast('Браузер подключён. Настройка сохранена');}
  });
  const regionLabel=document.createElement('label');regionLabel.textContent='Домен Яндекс Музыки';
  const region=document.createElement('select');region.id='yandex-domain';
  for(const [value,label] of [['auto','Авто — открытая вкладка / последний домен'],['music.yandex.ru','music.yandex.ru'],['music.yandex.kz','music.yandex.kz'],['music.yandex.com','music.yandex.com']]) {
    const option=document.createElement('option');option.value=value;option.textContent=label;region.append(option);
  }
  region.value=configuration.yandex_domain||'auto';
  region.addEventListener('change',async()=>{
    region.disabled=true;
    const result=await invoke('set_yandex_domain',region.value);
    region.disabled=false;
    if(result.ok){configuration.yandex_domain=region.value;toast('Домен Яндекс Музыки сохранён');}
    else {region.value=configuration.yandex_domain||'auto';showSettingsError(result.error);}
  });
  regionLabel.append(region);
  const regionNote=document.createElement('p');regionNote.textContent='Авто использует открытую музыкальную вкладку и запоминает её домен. При первом запуске без вкладки откроется music.yandex.ru.';
  section.append(title,state,note,regionLabel,regionNote,persist,button);$('shortcut-rows').append(section);
  renderBrowserConnection(configuration.browser);
}
function renderBrowserConnection(browser) {
  const state=$('browser-status'),button=$('connect-browser');if(!state||!button)return;
  browser=browser||{name:'Браузер Windows',state:'not_configured',message:'Подключите браузер один раз',supported:true};
  $('browser-name').textContent='Браузер по умолчанию · '+browser.name;
  state.textContent=browserSetupPending?'Подключение…':browser.message;
  state.dataset.state=browserSetupPending?'connecting':browser.state;
  const done=!!browser.connected&&!!browser.shortcuts_updated;
  button.textContent=browserSetupPending?'Подключение…':done?'Подключено':browser.connected?'Подключено · закрепить запуск':browser.configured?'Восстановить подключение':'Подключить браузер';
  button.disabled=browserSetupPending||done||browser.supported===false;
  $('browser-persist-shortcuts').disabled=browserSetupPending||done;
}
function renderOverlaySettings() {
  const section=document.createElement('section');section.className='overlay-options';
  section.innerHTML='<h3>Уведомления поверх игры</h3><label class="overlay-enabled"><input id="overlay-enabled" type="checkbox"> Показывать при скипе, лайке и паузе</label><div class="overlay-selects"><label>Угол экрана<select id="overlay-corner"><option value="top-right">Справа сверху</option><option value="top-left">Слева сверху</option><option value="bottom-right">Справа снизу</option><option value="bottom-left">Слева снизу</option></select></label><label>Время показа<select id="overlay-duration"><option value="3">3 секунды</option><option value="4">4 секунды</option><option value="6">6 секунд</option><option value="8">8 секунд</option></select></label></div><button id="overlay-preview" type="button">Проверить уведомление</button><p>Не забирает фокус и пропускает клики. Для игр используйте оконный или безрамочный режим.</p>';
  $('shortcut-rows').append(section);
  $('overlay-enabled').checked=editingOverlay.enabled;
  $('overlay-corner').value=editingOverlay.corner;
  $('overlay-duration').value=String(editingOverlay.duration);
  $('overlay-enabled').addEventListener('change',event=>editingOverlay.enabled=event.target.checked);
  $('overlay-corner').addEventListener('change',event=>editingOverlay.corner=event.target.value);
  $('overlay-duration').addEventListener('change',event=>editingOverlay.duration=Number(event.target.value));
  $('overlay-preview').addEventListener('click',async()=>{const result=await invoke('preview_overlay',editingOverlay);if(!result.ok)showSettingsError(result.error);});
}
function wireWheelSettings() {
  $('wheel-enabled').checked=!!editingWheel.enabled;
  $('wheel-modifier').value=editingWheel.modifier||'ctrl';
  $('wheel-step').value=String(editingWheel.step||5);
  $('wheel-enabled').onchange=e=>editingWheel.enabled=e.target.checked;
  $('wheel-modifier').onchange=e=>editingWheel.modifier=e.target.value;
  $('wheel-step').onchange=e=>editingWheel.step=Number(e.target.value);
}
function applyQuality(level){
  const q=['high','medium','low'].includes(level)?level:'high';
  document.body.dataset.quality=q;
  if(configuration)configuration.quality=q;
  document.querySelectorAll('.quality-tab').forEach(t=>t.classList.toggle('active',t.dataset.quality===q));
  applyFx();
}
async function setQuality(level){
  applyQuality(level);
  const result=await invoke('set_quality',level);
  if(!result.ok)toast(result.error);
  else toast(level==='high'?'Качество: высокое (максимум эффектов)':level==='medium'?'Качество: среднее (баланс)':'Качество: низкое (минимум нагрузки)');
}
function switchService(name){
  document.querySelectorAll('.service-tab').forEach(t=>t.classList.toggle('active',t.dataset.service===name));
  document.body.dataset.service=name;
  configuration.service=name;
  invoke('set_service',name);
  toast(name==='youtube'?'YouTube: откройте вкладку YouTube / YouTube Music':'Яндекс Музыка: откройте свою вкладку .ru, .kz или .com');
}
async function saveShortcuts() {
  const button=$('save-shortcuts');button.disabled=true;
  try {
    const result=await invoke('save_settings',editing,editingOverlay,editingWheel);
    if(!result.ok){showSettingsError(result.error);return;}
    configuration.bindings=clone(editing);configuration.overlay=clone(editingOverlay);configuration.wheel=clone(editingWheel);configuration.hotkey_error='';
    updateShortcuts();$('settings-dialog').close();toast('Горячие клавиши сохранены');
  } finally {button.disabled=false;}
}
const formatTime=value=>{value=Math.max(0,Math.floor(Number(value)||0));return Math.floor(value/60)+':'+String(value%60).padStart(2,'0');};
function renderState(snapshot) {
  currentState=snapshot;
  if(snapshot.browser&&configuration){configuration.browser=snapshot.browser;renderBrowserConnection(snapshot.browser);}
  renderVoice(snapshot.voice);
  const connected=!!snapshot.connected;
  const hasTrack=connected && snapshot.title && snapshot.title!=='Ожидаю воспроизведение';
  $('track-title').textContent=hasTrack?snapshot.title:'В ожидании музыки';
  $('track-title').title=hasTrack?snapshot.title:'';
  $('track-artist').textContent=hasTrack?snapshot.artist:'Включите любимый трек';
  $('track-artist').title=hasTrack?snapshot.artist:'';
  $('track-context').textContent=snapshot.playing && connected?'СЕЙЧАС ИГРАЕТ':'ВАША МУЗЫКА';
  const artwork=hasTrack?snapshot.artwork||'':'';
  if(artwork!==lastArtwork){
    lastArtwork=artwork;
    // Restrict incoming artwork to remote images; never navigate the webview.
    $('cover').src=/^https?:\/\//i.test(artwork)?artwork:'assets/cover.svg';
  }
  icon($('play-button'),snapshot.playing && connected?'pause':'play');
  $('play-button').setAttribute('aria-label',snapshot.playing?'Пауза':'Воспроизвести');
  $('like-button').setAttribute('aria-pressed',String(!!snapshot.liked && connected));
  $('like-button').setAttribute('aria-label',snapshot.liked?'Убрать из избранного':'Добавить в избранное');
  const duration=connected?Number(snapshot.duration)||0:0;
  const position=connected?Number(snapshot.position)||0:0;
  const progress=duration>0?Math.min(100,Math.max(0,position/duration*100)):0;
  $('position').textContent=formatTime(position);$('duration').textContent=formatTime(duration);
  $('progress-fill').style.width=progress+'%';
  document.querySelector('.progress-track').setAttribute('aria-valuenow',String(Math.round(progress)));
  $('connection-dot').classList.toggle('online',connected);
  const svcNow=(configuration && configuration.service) || snapshot.service || 'yandex';
  document.body.dataset.service=svcNow;
  document.querySelectorAll('.service-tab').forEach(t=>t.classList.toggle('active',t.dataset.service===svcNow));
  const svcShown=snapshot.service||'yandex';
  $('connection-title').textContent=connected?(svcShown==='youtube'?'YouTube подключён':'Яндекс Музыка подключена'):(svcNow==='youtube'?'Откройте YouTube или YouTube Music':'Откройте Яндекс Музыку');
  $('connection-detail').textContent=snapshot.error?snapshot.error:connected?(snapshot.browser?.name||configuration?.browser?.name||'Браузер')+' · без расширений':'Настройки → Подключить браузер';
  $('connection-detail').title=snapshot.error||'';
  const supported=connected && Number.isFinite(snapshot.volume);
  $('volume').disabled=!supported;
  if(!volumeDragging && supported){
    const volume=Math.round(Math.max(0,Math.min(1,snapshot.volume))*100);
    $('volume').value=volume;$('volume').style.setProperty('--volume',volume+'%');$('volume-value').textContent=volume+'%';
  }else if(!supported){$('volume-value').textContent='—';}
}
function renderVoice(voice) {
  if(!voice)return;
  const phase=voice.phase || 'idle';
  const cancellable=['preparing','listening','recognizing'].includes(phase);
  const busy=cancellable || ['waiting','cancelling'].includes(phase);
  const button=$('voice-button');
  button.dataset.phase=phase;
  button.setAttribute('aria-pressed',String(cancellable));
  button.setAttribute('aria-label',cancellable?'Отменить голосовую команду':'Начать голосовую команду');
  button.disabled=busy && !cancellable;
  $('voice-status').textContent=voice.message;
  $('voice-transcript').textContent=voice.text?'Вы сказали: «'+voice.text+'»':'';
  $('voice-transcript').hidden=!voice.text;
}
$('voice-button').addEventListener('click',async()=>{
  const button=$('voice-button');
  const cancel=['preparing','listening','recognizing'].includes(button.dataset.phase);
  button.disabled=true;
  const result=await invoke(cancel?'cancel_voice':'start_voice');
  if(!result.ok)toast(result.error);
  button.disabled=false;
  await poll();
});
async function poll() {
  if(!api || busyPoll)return;
  busyPoll=true;
  try{const snapshot=await api.get_state();renderState(snapshot);}catch(_){$('connection-title').textContent='Связь с виджетом потеряна';}
  finally{busyPoll=false;}
}
/* ---------- жидкое стекло: какие элементы рисует шейдер ---------- */
const playTint=()=>document.body.dataset.service==='youtube'?[.34,.07,.10]:[.07,.14,.34];
LiquidGlass.mount({
  canvas:$('lg-canvas'),root:document.querySelector('.shell'),poster:LiquidPosters.main,
  // bevel — ширина скоса кромки, strength — «толщина» стекла (сила преломления), dark — затемнение интерьера
  kinds:[
    {selector:'.player,.preferences,.footer',bevel:24,strength:44,dark:.58,tint:[.02,.03,.07],blur:3.2},
    {selector:'.genre',bevel:16,strength:28,dark:.66,tint:[.03,.04,.08],blur:2.6,clip:'.genres'},
    {selector:'.icon-button',bevel:13,strength:20,dark:.84,tint:[.04,.05,.10],blur:1.6},
    {selector:'.play-button',bevel:22,strength:34,dark:.80,tint:playTint,blur:2.0}
  ]
});
/* настройки эффекта: glass — шейдерное стекло, clear — прозрачное окно с реальным фоном за стеклом */
var fx={glass:true,clear:true};
function applyFx(){
  const quality=document.body.dataset.quality||'high';
  const glassOn=fx.glass&&quality!=='low';
  const clear=glassOn&&fx.clear;
  document.body.classList.toggle('lg-clear',clear);
  LiquidGlass.setSolid(!clear);
  LiquidGlass.setQuality(glassOn?quality:'low');
  const g=$('fx-glass'),c=$('fx-clear');
  if(g)g.checked=fx.glass;
  if(c){c.checked=fx.clear;c.disabled=!glassOn;$('fx-clear-row').style.opacity=glassOn?'':'.45';}
  syncBackdrop();
}
async function changeFx(patch){
  const previous=fx;fx={...fx,...patch};applyFx();
  const result=await invoke('set_fx',fx);
  if(!result.ok){fx=previous;applyFx();toast(result.error);}
}
applyQuality('high');
/* Optional native desktop backdrop. */
var backdropTimer=0,backdropBusy=false;
async function pullBackdrop(){
  if(backdropBusy||!api)return;backdropBusy=true;
  try{const r=await api.get_backdrop();if(r&&r.ok&&r.data)LiquidGlass.setBackdrop(r.data);}catch(_){}
  finally{backdropBusy=false;}
}
function syncBackdrop(){
  clearInterval(backdropTimer);backdropTimer=0;
  const clear=document.body.classList.contains('lg-clear');
  const on=clear&&api&&typeof api.get_backdrop==='function'&&!document.hidden;
  if(on){pullBackdrop();backdropTimer=setInterval(pullBackdrop,1200);}else if(!clear)LiquidGlass.clearBackdrop();
}
document.addEventListener('visibilitychange',syncBackdrop);
async function connect() {
  if(api)return;
  api=window.liquid?.api;
  if(!api)return;
  configuration=await invoke('get_settings');
  if(!configuration.bindings){toast(configuration.error||'Не удалось загрузить настройки');api=null;return;}
  $('on-top').checked=configuration.on_top;$('startup').checked=configuration.startup;
  renderGenres(configuration.genres);updateShortcuts();
  fx={...fx,...(configuration.fx||{})};
  applyQuality(configuration.quality || 'high');
  if(configuration.hotkey_error)toast(configuration.hotkey_error);
  await poll();
  // Скрытое окно не опрашиваем: каждый вызов api.get_state идёт через GUI-поток.
  let pollTimer=setInterval(poll,700);
  document.addEventListener('visibilitychange',()=>{
    if(document.hidden){clearInterval(pollTimer);pollTimer=0;}
    else if(!pollTimer){poll();pollTimer=setInterval(poll,700);}
  });
}
$('settings-button').addEventListener('click',showSettings);
$('keys-button').addEventListener('click',showSettings);
document.querySelectorAll('.service-tab').forEach(t=>t.addEventListener('click',()=>switchService(t.dataset.service)));
$('dismiss-settings').addEventListener('click',()=>$('settings-dialog').close());
$('settings-dialog').addEventListener('click',event=>{if(event.target===$('settings-dialog'))$('settings-dialog').close();});
$('save-shortcuts').addEventListener('click',saveShortcuts);
$('reset-shortcuts').addEventListener('click',()=>{editing=clone(defaultBindings);renderShortcutRows();$('settings-error').hidden=true;});
$('close-button').addEventListener('click',()=>invoke('hide'));
$('quit-button').addEventListener('click',()=>invoke('quit'));
$('like-button').addEventListener('click',()=>command('like'));
$('open-music').addEventListener('click',async()=>{const result=await invoke('open_music');if(!result.ok)toast(result.error);else toast('Музыкальная вкладка готова в браузере');});
document.querySelectorAll('[data-command]').forEach(el=>el.addEventListener('click',()=>command(el.dataset.command)));
[['on-top','set_topmost'],['startup','set_startup']].forEach(([id,method])=>{
  $(id).addEventListener('change',async event=>{
    const checked=event.target.checked;event.target.disabled=true;
    const result=await invoke(method,checked);
    if(!result.ok){event.target.checked=!checked;toast(result.error);}
    event.target.disabled=false;
  });
});
$('volume').addEventListener('input',()=>{volumeDragging=true;const value=Number($('volume').value);$('volume-value').textContent=value+'%';$('volume').style.setProperty('--volume',value+'%');});
$('volume').addEventListener('change',async()=>{await command('volume',Number($('volume').value)/100);setTimeout(()=>volumeDragging=false,700);});
document.querySelectorAll('.quality-tab').forEach(t=>t.addEventListener('click',()=>setQuality(t.dataset.quality)));
$('fx-glass').addEventListener('change',e=>changeFx({glass:e.target.checked}));
$('fx-clear').addEventListener('change',e=>changeFx({clear:e.target.checked}));
$('cover').addEventListener('error',()=>{if(!$('cover').src.endsWith('/assets/cover.svg'))$('cover').src='assets/cover.svg';});
document.addEventListener('keydown',event=>{
  if($('settings-dialog').open)return;
  if(event.code==='Escape'){event.preventDefault();invoke('hide');}
  if(event.code==='Space' && !['INPUT','SELECT','BUTTON'].includes(document.activeElement.tagName)){event.preventDefault();command('play_pause');}
});
renderGenres(fallbackGenres);
window.addEventListener('liquidready',connect);
if(window.liquid?.api)connect();
