'use strict';
const $ = id => document.getElementById(id);
const clone = value => JSON.parse(JSON.stringify(value));
const defaultBindings = Object.fromEntries([['toggle','Num0'],['next','Num6'],['previous','Num4'],['play_pause','Num5'],['stop','Num2'],['like','Num1'],['voice','Num3']].map(([action,key]) => [action,{key,ctrl:false,alt:false,shift:false}]));
let wavesKey=null,wavesAttempted=false,wavesLoading=false,waveSelecting=false,waveOrigin=null;
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
function renderWaves(waves=[],message='Подключите Яндекс Музыку, чтобы увидеть подборки Моей волны.') {
  const key=JSON.stringify([waves,waves.length?'':message]);if(key===wavesKey)return;wavesKey=key;
  $('genres').replaceChildren();
  if(!waves.length){const hint=document.createElement('p');hint.className='wave-empty';hint.textContent=message;$('genres').append(hint);return;}
  waves.forEach((wave,index)=>{
    const row=document.createElement('button');row.type='button';row.className='genre';
    row.title='Включить волну: '+wave.title;row.setAttribute('aria-label',row.title);row.disabled=!!wave.disabled;
    const img=document.createElement('img');img.alt='';img.referrerPolicy='no-referrer';
    const fallback='assets/genre-'+(index%14)+'.svg';
    img.src=/^https:\/\//i.test(wave.artwork||'')?wave.artwork:fallback;
    img.addEventListener('error',()=>{if(!img.src.endsWith('/'+fallback))img.src=fallback;});
    const text=document.createElement('span');text.className='genre-name';text.textContent=wave.title;
    const arrow=document.createElementNS('http://www.w3.org/2000/svg','svg');
    const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#i-chevron');arrow.append(use);row.append(img,text,arrow);
    row.addEventListener('click',async()=>{
      if(wavesLoading||waveSelecting)return;waveSelecting=true;row.disabled=true;
      try{
        const result=await command('wave',wave.id);
        if(result.ok){document.querySelectorAll('.genre').forEach(el=>el.classList.remove('selected'));row.classList.add('selected');toast('Выбрана волна «'+wave.title+'»');}
      }finally{waveSelecting=false;row.disabled=!!wave.disabled;}
    });
    $('genres').append(row);
  });
}
async function refreshWaves() {
  if(wavesLoading)return;wavesLoading=true;wavesAttempted=true;
  const button=$('refresh-waves');button.disabled=true;button.textContent='Загрузка…';
  try{
    const result=await invoke('get_waves');
    if((configuration?.service||'yandex')!=='yandex')return;
    if(result.ok){waveOrigin=result.wave_origin||waveOrigin;renderWaves(result.waves||[],'В Моей волне пока нет доступных подборок.');}
    else renderWaves([],result.error||'Не удалось загрузить Мою волну. Обновите список.');
  }finally{wavesLoading=false;button.disabled=false;button.textContent='Обновить';}
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
  const title=document.createElement('h3');title.textContent='Умный помощник · Gemini / DeepSeek';
  const note=document.createElement('p');note.textContent='Понимает свободные фразы и уточнения, учитывает текущий трек. Текст сложных запросов, данные плеера и короткая история отправляются в выбранный сервис. Gemini доступен в рамках Free Tier с квотами; доступность зависит от региона и аккаунта. На Free Tier данные могут использоваться Google для улучшения продуктов. DeepSeek требует API-баланс. Простые команды работают без интернета. Ответы показаны текстом.';
  const provider=document.createElement('select');provider.id='assistant-provider';provider.setAttribute('aria-label','Сервис умного помощника');
  for(const [value,label] of [['gemini','Gemini · Google'],['deepseek','DeepSeek']]){const option=document.createElement('option');option.value=value;option.textContent=label;provider.append(option);}
  const enabledLabel=document.createElement('label');enabledLabel.className='overlay-enabled';
  const enabled=document.createElement('input');enabled.type='checkbox';enabled.id='deepseek-enabled';enabled.checked=!!configuration.deepseek?.enabled;
  enabledLabel.append(enabled,document.createTextNode('Использовать модель для свободных фраз'));
  const key=document.createElement('input');key.type='password';key.id='deepseek-key';key.autocomplete='off';key.setAttribute('aria-label','API-ключ выбранного помощника');
  const status=document.createElement('p');status.id='deepseek-status';status.setAttribute('role','status');
  const save=document.createElement('button');save.type='button';save.id='save-deepseek';save.textContent='Сохранить помощника';
  const test=document.createElement('button');test.type='button';test.id='test-deepseek';test.textContent='Проверить подключение';
  const remove=document.createElement('button');remove.type='button';remove.id='delete-deepseek';remove.textContent='Удалить ключ';
  const audioLabel=document.createElement('label');audioLabel.className='overlay-enabled';
  const audio=document.createElement('input');audio.type='checkbox';audio.id='gemini-audio';
  audioLabel.append(audio,document.createTextNode('Русский + English: распознавать запись через Gemini'));
  const audioNote=document.createElement('p');audioNote.textContent='По умолчанию звук распознаёт локальная русская модель Vosk. При включении этого режима короткие записи музыкальных просьб отправляются Google и расходуют квоту Gemini. Это помогает с английскими названиями и артистами; точные простые команды остаются локальными.';
  const hintsLabel=document.createElement('label');hintsLabel.textContent='Подсказки имён артистов';
  const hints=document.createElement('textarea');hints.id='voice-artist-hints';hints.rows=2;hints.maxLength=1000;hints.setAttribute('aria-label','Подсказки имён артистов');hintsLabel.append(hints);
  function paint(){const s=configuration.deepseek||{};provider.value=s.provider||'deepseek';enabled.checked=!!s.enabled;audio.checked=!!s.audio_enabled;audio.disabled=hints.disabled=provider.value!=='gemini';hints.value=s.artist_hints??'madk1d (мэд кид, мэдкид, мэт кид); VILLIAN';key.placeholder=s.configured?'Ключ сохранён — введите новый для замены':'API-ключ '+(provider.value==='gemini'?'Gemini':'DeepSeek');status.textContent=s.configured?(s.enabled?'Включён · ':'Ключ сохранён · ')+(s.model||provider.value):'Ключ не настроен';test.disabled=remove.disabled=!s.configured;}
  async function update(method,...args){
    save.disabled=test.disabled=remove.disabled=enabled.disabled=provider.disabled=key.disabled=audio.disabled=hints.disabled=true;
    try {
      const result=await invoke(method,...args);
      if(result.deepseek)configuration.deepseek=result.deepseek;
      if(result.ok){key.value='';paint();if(method==='test_deepseek')status.textContent='Подключение проверено · '+configuration.deepseek.model;}
      else {paint();showSettingsError(result.error);}
    } finally {save.disabled=enabled.disabled=provider.disabled=key.disabled=false;audio.disabled=hints.disabled=provider.value!=='gemini';test.disabled=remove.disabled=!configuration.deepseek?.configured;}
  }
  save.addEventListener('click',()=>update('save_deepseek',key.value,enabled.checked,audio.checked,hints.value));
  test.addEventListener('click',()=>update('test_deepseek'));
  remove.addEventListener('click',()=>update('delete_deepseek'));
  provider.addEventListener('change',()=>update('set_assistant_provider',provider.value));
  section.append(title,note,provider,enabledLabel,key,audioLabel,audioNote,hintsLabel,status,save,test,remove);$('shortcut-rows').append(section);paint();
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
  applyTheme(name);
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
  const waveService=configuration?.service||snapshot.service||'yandex';
  document.querySelector('.genres-section').hidden=waveService!=='yandex';
  if(waveService==='yandex'){
    if(waveOrigin&&snapshot.wave_origin&&waveOrigin!==snapshot.wave_origin){wavesAttempted=false;renderWaves();}
    if(snapshot.wave_origin)waveOrigin=snapshot.wave_origin;
    if(snapshot.waves?.length){renderWaves(snapshot.waves);wavesAttempted=true;}
    else if(connected&&!wavesAttempted&&!wavesLoading)void refreshWaves();
    if(!connected){wavesAttempted=false;waveOrigin=null;renderWaves();}
  }
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
  applyTheme(svcNow);
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
// тема: подключаем доп. стили (розово-фиолетовые акценты), не трогая style.css
if(!document.querySelector('link[href$="aurora.css"]')){const l=document.createElement('link');l.rel='stylesheet';l.href='aurora.css';document.head.append(l);}
const playTint=()=>document.body.dataset.service==='youtube'?[.34,.06,.08]:[.30,.05,.24];
// палитра авроры (от тёмного к светлому): розово-фиолетовая и красная для YouTube
const PALETTES={
  yandex:[[.17,.04,.11],[.47,.13,.52],[.78,.33,.70],[1,.45,.52],[1,.68,.82],[1,.89,.93]],
  youtube:[[.16,.03,.05],[.50,.08,.14],[.82,.18,.25],[1,.40,.30],[1,.65,.50],[1,.88,.80]]
};
let paletteName='';
function applyTheme(name){
  name=name==='youtube'?'youtube':'yandex';
  if(name===paletteName)return;
  paletteName=name;LiquidGlass.setPalette(PALETTES[name]);
}
LiquidGlass.mount({
  canvas:$('lg-canvas'),root:document.querySelector('.shell'),palette:PALETTES.yandex,solid:true,
  bgAlpha:.62,glassAlpha:.84,aurora:.8,
  // bevel — ширина скоса кромки, strength — «толщина» стекла (сила преломления), dark — затемнение интерьера
  kinds:[
    {selector:'.shell',frame:true,bevel:46,strength:120,tint:[0,0,0]},   // кромка окна: линза, искажающая рабочий стол
    {selector:'.player,.preferences,.footer',bevel:24,strength:44,dark:.56,tint:[.04,.01,.05],blur:3.0},
    {selector:'.genre',bevel:16,strength:28,dark:.62,tint:[.04,.01,.05],blur:2.4,clip:'.genres'},
    {selector:'.icon-button',bevel:13,strength:20,dark:.70,tint:[.05,.01,.06],blur:1.4},
    {selector:'.play-button',bevel:22,strength:34,dark:.72,tint:playTint,blur:1.8}
  ]
});
/* настройки эффекта: glass — шейдерное стекло, clear — фон окна «просвечивает» рабочим столом (снимок от приложения), anim — анимация авроры */
var fx={glass:true,clear:true,anim:true};
function ensureFxUi(){
  if($('fx-anim'))return;
  const anchor=$('fx-clear-row')||($('fx-clear')&&$('fx-clear').closest('label'));if(!anchor)return;
  const row=document.createElement('label');row.className='preference';row.id='fx-anim-row';
  row.innerHTML='<svg><use href="#i-gauge"/></svg><span>Анимация фона</span><input id="fx-anim" type="checkbox" class="switch" checked><span class="switch-track" aria-hidden="true"></span>';
  anchor.after(row);
  $('fx-anim').addEventListener('change',e=>changeFx({anim:e.target.checked}));
}
function applyFx(){
  const quality=document.body.dataset.quality||'high';
  const glassOn=fx.glass&&quality!=='low';
  const clear=glassOn&&fx.clear;
  document.body.classList.toggle('lg-clear',clear);
  document.body.classList.toggle('lg-glass',glassOn);
  LiquidGlass.setBgAlpha(clear?.58:1);   // «прозрачное окно» = фон-аврора просвечивает; кромка искажает рабочий стол всегда
  LiquidGlass.setSolid(true);                 // окно непрозрачное; «прозрачность» даёт снимок рабочего стола
  LiquidGlass.setAnimated(fx.anim);
  LiquidGlass.setQuality(glassOn?quality:'low');
  const g=$('fx-glass'),c=$('fx-clear'),an=$('fx-anim');
  if(g)g.checked=fx.glass;
  if(an){an.checked=fx.anim;an.disabled=!glassOn;}
  if(c){c.checked=fx.clear;c.disabled=!glassOn;const r=$('fx-clear-row');if(r)r.style.opacity=glassOn?'':'.45';}
  syncBackdrop();
}
async function changeFx(patch){
  const previous=fx;fx={...fx,...patch};applyFx();
  const result=await invoke('set_fx',fx);
  if(!result.ok){fx=previous;applyFx();toast(result.error);}
}
ensureFxUi();
applyQuality('high');
/* снимок рабочего стола под окном: приложение исключает окно из захвата и присылает JPEG + размеры/позицию */
var backdropTimer=0,backdropBusy=false;
async function pullBackdrop(){
  if(backdropBusy||!api)return;backdropBusy=true;
  try{const r=await api.get_backdrop();if(r&&r.ok&&r.data)LiquidGlass.setBackdrop(r.data,{w:r.w,h:r.h,pad:r.pad,left:r.left,top:r.top});}catch(_){}
  finally{backdropBusy=false;}
}
function syncBackdrop(){
  clearInterval(backdropTimer);backdropTimer=0;
  const glass=document.body.classList.contains('lg-glass');
  const on=glass&&api&&!document.hidden;
  if(on){pullBackdrop();backdropTimer=setInterval(pullBackdrop,700);}else if(!glass)LiquidGlass.clearBackdrop();
}
document.addEventListener('visibilitychange',syncBackdrop);
async function connect() {
  if(api)return;
  api=window.liquid?.api;
  if(!api)return;
  configuration=await invoke('get_settings');
  if(!configuration.bindings){toast(configuration.error||'Не удалось загрузить настройки');api=null;return;}
  $('on-top').checked=configuration.on_top;$('startup').checked=configuration.startup;
  window.LiquidMood?.configure(configuration.liquid_mood);
  updateShortcuts();
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
$('refresh-waves').addEventListener('click',refreshWaves);
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
renderWaves();
window.addEventListener('liquidready',connect);
if(window.liquid?.api)connect();
