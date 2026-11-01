let playwright;
try { playwright=require('playwright'); } catch (_) { playwright=require(require('node:path').join(require('node:os').homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')); }
const {chromium}=playwright;
const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const {pathToFileURL} = require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:path.join(process.env['ProgramFiles(x86)']||'C:/Program Files (x86)','Microsoft/Edge/Application/msedge.exe')});
 const page=await browser.newPage({viewport:{width:414,height:1000},deviceScaleFactor:1});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
   const bindings=Object.fromEntries([['toggle','Num0'],['next','Num6'],['previous','Num4'],['play_pause','Num5'],['stop','Num2'],['like','Num1'],['voice','Num3']].map(([a,key])=>[a,{key,ctrl:false,alt:false,shift:false}]));
   window.voice={phase:'idle',message:'Скажите: «включи трек Династия»',text:''};
   window.calls=[];
   window.spotifyAccount='account-a';window.spotifySaved={};
   window.spotifyLists=[{id:'37i9dQZF1DXcBWIGoYBM5M',url:'https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M',title:'Daily Mix 1',artwork:''},{id:'37i9dQZF1E35tjSnE7z5If',url:'https://open.spotify.com/playlist/37i9dQZF1E35tjSnE7z5If',title:'Мои любимые треки',artwork:''}];
   window.waves=['Это поп!','Саундскейп','Незнакомое','Бежать быстрее ветра','Артист Real Girl','Встряхнуть Мою волну','Русский рок','Хочется электроники','Мистическое настроение','Инди','Метал','Джаз','Релакс','Ретро'].map(title=>({id:title,title,artwork:''}));
   window.browserStatus={name:'Brave',supported:true,configured:false,connected:false,state:'not_configured',message:'Подключите браузер один раз',shortcuts_updated:false};
   window.liquid={api:{
     get_settings:async()=>({backend:"csharp",browser:window.browserStatus,bindings,actions:{toggle:'Показать / скрыть',next:'Следующий трек',previous:'Предыдущий трек',play_pause:'Пауза / продолжить',stop:'Остановить воспроизведение',like:'Лайк / убрать лайк',voice:'Голосовая команда'},keys:['Num0','Num1','Num2','Num3','Num4','Num5','Num6','F6','F8'],genres:['Хип-хоп','Рок','Электроника','Поп','Инди','Метал','Альтернатива','Джаз','Релакс','Лоу-фай','R&B','Классика','Техно','Ретро'],on_top:true,startup:false}),
     get_state:async()=>({title:'NIGHT DRIVE',artist:'PHARAOH',playing:true,liked:false,connected:!window.browserStatus.embedded||!!window.browserStatus.signed_in,artwork:'',position:102,duration:197,volume:.68,voice:window.voice,browser:window.browserStatus,waves:window.waves,wave_origin:'https://music.yandex.kz',active_wave:window.activeWave||null,service:window.service||'yandex',spotify_account:window.spotifyAccount,active_spotify_playlist:window.spotifyActive||null}),
     get_design:async()=>({ok:true,design:window.design||'aurora'}),
     set_design:async(design)=>{window.design=design;return {ok:true,design};},
     set_service:async(service)=>{window.service=service;return {ok:true};},
     get_discord:async()=>({ok:true,enabled:false,status:'disabled'}),
     get_waves:async()=>window.waveError?{ok:false,error:'Войдите в Яндекс Музыку'}:{ok:true,waves:window.waves,wave_origin:'https://music.yandex.kz'},
     get_spotify_playlists:async()=>({ok:true,account:window.spotifyAccount,playlists:[...(window.spotifyAccount==='account-a'?window.spotifyLists:[]),...(window.spotifySaved[window.spotifyAccount]||[])]}),
     add_spotify_playlist:async url=>{
       if(!/^https:\/\/open\.spotify\.com\/playlist\/[A-Za-z0-9]{22}(?:\?|$)/.test(url))return {ok:false,error:'Нужна ссылка на плейлист Spotify'};
       const saved=window.spotifySaved[window.spotifyAccount]||=[];const canonical=url.split('?')[0];const added=!saved.some(p=>p.url===canonical);
       if(added)saved.push({id:canonical.split('/').at(-1),url:canonical,title:'Added from link',artwork:''});
       return {ok:true,added,account:window.spotifyAccount,playlists:[...(window.spotifyAccount==='account-a'?window.spotifyLists:[]),...saved]};
     },
     start_voice:async()=>{window.voice={phase:'listening',message:'Слушаю…',text:''};return {ok:true};},
     cancel_voice:async()=>{window.voice={phase:'idle',message:'Запись отменена',text:''};return {ok:true};},
     command:async(...args)=>{window.calls.push(args);if(args[0]==='spotify_playlist')window.spotifyActive=args[1].split('/').at(-1);if(args[0]==='wave')window.activeWave=args[1];if(args[0]==='wave_reset'){if(window.failWaveReset)return {ok:false,error:'Не удалось включить Мою волну'};window.activeWave=null;}return {ok:true};},
     setup_browser:async(persist)=>{
       if(window.browserStatus.embedded){window.accountShown=true;return {ok:true,browser:window.browserStatus};}
       if(window.cancelSetup)return {ok:false,cancelled:true,browser:window.browserStatus};
       if(window.failSetup)return {ok:false,error:'Ошибка подключения',browser:window.browserStatus};
       window.browserStatus={name:'Brave',supported:true,configured:true,connected:true,state:'connected',message:'Подключено',shortcuts_updated:persist};
       return {ok:true,browser:window.browserStatus};
     },
     save_settings:async(b,o,w)=>{window.saved=b;window.savedOverlay=o;window.savedWheel=w;return {ok:true};},
     set_yandex_domain:async(domain)=>{window.yandexDomain=domain;return {ok:true};},
     set_assistant_provider:async(provider)=>{window.assistantProvider=provider;return {ok:true,deepseek:{provider,enabled:false,configured:false}};},
     save_deepseek:async(key,enabled,audio,hints)=>{window.assistantKey=key;window.assistantEnabled=enabled;window.audioEnabled=audio;window.artistHints=hints;return {ok:true,deepseek:{provider:window.assistantProvider,enabled,configured:true,model:'gemini-3.5-flash-lite',audio_enabled:audio,artist_hints:hints}};},
     test_deepseek:async()=>({ok:true,deepseek:{provider:window.assistantProvider,enabled:true,configured:true,model:'gemini-3.5-flash-lite'}}),
     delete_deepseek:async()=>({ok:true,deepseek:{provider:window.assistantProvider,enabled:false,configured:false}}),
     hide:async()=>({ok:true}),quit:async()=>({ok:true}),set_topmost:async()=>({ok:true}),set_startup:async()=>({ok:true})
   }};
 });
 await page.addInitScript({path:path.resolve('ui/design.js')});
 await page.addInitScript({path:path.resolve('ui/discord-settings.js')});
 await page.route('https://liquid.local/**',async route=>{
   const file=path.join(path.resolve('ui'),new URL(route.request().url()).pathname);
   const types={'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml'};
   if(!fs.existsSync(file)){await route.abort();return;}
   await route.fulfill({body:fs.readFileSync(file),contentType:types[path.extname(file)]||'application/octet-stream'});
 });
 await page.goto('https://liquid.local/index.html');
 await page.evaluate(()=>window.dispatchEvent(new Event('liquidready')));
 await page.waitForFunction(()=>document.getElementById('track-title').textContent==='NIGHT DRIVE');
 await page.waitForFunction(()=>Array.from(document.querySelectorAll('.service-icon img')).every(img=>img.complete&&img.naturalWidth>0));
 assert.equal(await page.locator('.service-icon img').count(),4,'All four official platform icons load from bundled assets');
 await page.evaluate(()=>window.LiquidMood.configure({enabled:true}));
 assert.ok(await page.locator('#liquid-mood').isVisible());
 assert.ok(await page.locator('#volume').isVisible(),'Volume remains on the main screen with Liquid Mood open');
 await page.locator('[data-mood=dismiss]').click();
 assert.equal(await page.locator('.genre').count(),14);
 assert.equal(await page.locator('.genres-section h2').first().textContent(),'Моя волна');
 assert.equal(await page.locator('.genre-name').first().textContent(),'Это поп!');
 await page.locator('#voice-button').click();
 await page.waitForFunction(()=>document.getElementById('voice-button').getAttribute('aria-pressed')==='true');
 assert.equal(await page.locator('#voice-status').textContent(),'Слушаю…');
 await page.locator('#voice-button').click();
 await page.waitForFunction(()=>document.getElementById('voice-status').textContent==='Запись отменена');
 await page.evaluate(()=>{window.voice={phase:'waiting',message:'Ищу и включаю: Династия',text:'включи трек Династия'};});
 await page.waitForFunction(()=>document.getElementById('voice-button').disabled);
 assert.ok((await page.locator('#voice-transcript').textContent()).includes('Династия'));
 await page.evaluate(()=>{window.voice={phase:'done',message:'Выполнено: Династия',text:'включи трек Династия'};});
 await page.waitForFunction(()=>!document.getElementById('voice-button').disabled);
 await page.locator('[data-command=next]').click();
 await page.locator('#stop-button').click();
 await page.locator('#like-button').click();
 assert.deepEqual(await page.evaluate(()=>window.calls.map(c=>c[0])),['next','stop','like']);
 await page.locator('.genre').nth(0).click();
 assert.equal(await page.locator('.genre.selected').count(),1);
 assert.deepEqual(await page.evaluate(()=>window.calls.at(-1)),['wave','Это поп!'],'Wave selection uses its exact ID, never genre search');
 assert.equal(await page.locator('#track-context').textContent(),'Это поп! ×');
 await page.waitForTimeout(1300);
 assert.equal(await page.locator('.genre.selected').count(),1,'Polling preserves selected genre');
 await page.evaluate(()=>{window.failWaveReset=true;});
 await page.locator('#track-context').click();
 assert.equal(await page.locator('#track-context').textContent(),'Это поп! ×','Failed reset retains badge');
 assert.equal(await page.locator('.genre.selected').count(),1);
 await page.evaluate(()=>{window.failWaveReset=false;});
 await page.locator('#track-context').press('Enter');
 assert.deepEqual(await page.evaluate(()=>window.calls.at(-1)),['wave_reset',null]);
 assert.equal(await page.locator('.genre.selected').count(),0);
 assert.equal(await page.locator('#track-context').textContent(),'СЕЙЧАС ИГРАЕТ');
 assert.ok(await page.locator('#track-context').isDisabled());
 await page.locator('.genre').first().click();
 await page.evaluate(()=>{window.savedWaves=window.waves;window.waves=[];window.waveError=true;});
 await page.locator('#refresh-waves').click();
 assert.equal(await page.locator('.genre').count(),0);
 assert.equal(await page.locator('.wave-empty').textContent(),'Войдите в Яндекс Музыку');
 await page.evaluate(()=>{window.waves=window.savedWaves;window.waveError=false;});
 await page.locator('#refresh-waves').click();
 assert.equal(await page.locator('.genre').count(),14);
 assert.equal(await page.locator('.genre.selected').count(),1,'Refreshing cards retains selection');
 await page.evaluate(()=>document.getElementById('toast').hidden=true);
 await page.screenshot({path:'tests/ui-preview.png'});
 await page.locator('#settings-button').click();
 assert.equal(await page.locator('#design-settings').count(),1);
 assert.equal(await page.locator('#discord-settings').count(),1);
 assert.equal(await page.locator('#settings-dialog #fx-anim').count(),1);
 assert.equal(await page.locator('[data-settings-tab]').count(),7);
 assert.equal(await page.locator('.settings-tabpanel:visible').count(),1);
 assert.ok(await page.locator('#settings-controls').isVisible());
 await page.locator('#key-next').selectOption('F8');
 await page.locator('#wheel-modifier').selectOption('alt');
 for(const name of ['appearance','browser','assistant','island','discord','mood']){
   await page.locator('[data-settings-tab='+name+']').click();
   assert.ok(await page.locator('#settings-'+name).isVisible());
   assert.equal(await page.locator('.settings-tabpanel:visible').count(),1);
 }
 await page.locator('[data-settings-tab=mood]').press('Home');
 assert.ok(await page.locator('#settings-controls').isVisible());
 assert.equal(await page.locator('#key-next').inputValue(),'F8','Hotkey draft survives switching tabs');
 assert.equal(await page.locator('#wheel-modifier').inputValue(),'alt','Wheel draft survives switching tabs');
 await page.locator('[data-settings-tab=controls]').press('End');
 assert.ok(await page.locator('#settings-mood').isVisible());
 await page.locator('[data-settings-tab=mood]').press('ArrowRight');
 assert.ok(await page.locator('#settings-controls').isVisible());
 await page.locator('[data-settings-tab=appearance]').click();
 await page.locator('#design-settings [data-design=abyss]').click();
 await page.waitForFunction(()=>document.body.dataset.design==='abyss');
 await page.locator('#design-settings [data-design=aurora]').click();
 await page.waitForFunction(()=>document.body.dataset.design==='aurora');
 await page.locator('#save-shortcuts').scrollIntoViewIfNeeded();
 assert.equal(await page.locator('.shortcut-row').count(),7);
 await page.locator('[data-settings-tab=assistant]').click();
 assert.equal(await page.locator('#deepseek-key').getAttribute('type'),'password');
 assert.equal(await page.locator('#gemini-audio').isChecked(),false,'Cloud audio is opt-in');
 await page.locator('#deepseek-key').fill('unsaved-key');
 await page.locator('[data-settings-tab=browser]').click();
 await page.locator('[data-settings-tab=assistant]').click();
 assert.equal(await page.locator('#deepseek-key').inputValue(),'unsaved-key','Assistant draft survives switching tabs');
 await page.locator('#assistant-provider').selectOption('gemini');
 await page.waitForFunction(()=>window.assistantProvider==='gemini');
 assert.equal(await page.locator('#deepseek-key').inputValue(),'','Provider switch clears unsaved key');
 await page.locator('#deepseek-key').scrollIntoViewIfNeeded();
 await page.screenshot({path:'tests/assistant-preview.png'});
 await page.locator('#deepseek-key').fill('test-key-not-a-real-secret');
 await page.locator('#deepseek-enabled').check();
 await page.locator('#gemini-audio').check();
 await page.locator('#voice-artist-hints').fill('madk1d (мэд кид); VILLIAN');
 await page.locator('#save-deepseek').click();
 await page.waitForFunction(()=>window.assistantEnabled===true);
 assert.equal(await page.evaluate(()=>window.audioEnabled),true);
 assert.ok((await page.evaluate(()=>window.artistHints)).includes('madk1d'));
 assert.equal(await page.locator('#deepseek-key').inputValue(),'','Saved key is cleared from DOM');
 await page.locator('#test-deepseek').click();
 await page.waitForFunction(()=>document.getElementById('deepseek-status').textContent.includes('проверено'));
 await page.locator('#delete-deepseek').click();
 await page.waitForFunction(()=>document.getElementById('deepseek-status').textContent==='Ключ не настроен');
 await page.locator('[data-settings-tab=browser]').click();
 assert.equal(await page.locator('#yandex-domain').inputValue(),'auto');
 await page.locator('#yandex-domain').selectOption('music.yandex.ru');
 await page.waitForFunction(()=>window.yandexDomain==='music.yandex.ru');
 await page.locator('#yandex-domain').selectOption('music.yandex.kz');
 await page.waitForFunction(()=>window.yandexDomain==='music.yandex.kz');
 assert.equal(await page.locator('#connect-browser').textContent(),'Подключить браузер');
 await page.evaluate(()=>window.cancelSetup=true);
 await page.locator('#connect-browser').click();
 assert.equal(await page.locator('#connect-browser').textContent(),'Подключить браузер','Cancellation never displays Connected');
 await page.evaluate(()=>{window.cancelSetup=false;window.failSetup=true;});
 await page.locator('#connect-browser').click();
 assert.equal(await page.locator('#browser-status').textContent(),'Подключите браузер один раз');
 assert.equal(await page.locator('#settings-error').textContent(),'Ошибка подключения');
 await page.evaluate(()=>window.failSetup=false);
 await page.locator('#connect-browser').click();
 assert.equal(await page.locator('#browser-status').textContent(),'Подключено');
 assert.equal(await page.locator('#connect-browser').textContent(),'Подключено');
 assert.equal(await page.locator('#connect-browser').isDisabled(),true);
 await page.locator('#save-shortcuts').click();
 assert.equal(await page.evaluate(()=>window.saved.next.key),'F8');
 assert.equal(await page.evaluate(()=>window.savedWheel.modifier),'alt','Global save includes changes from another tab');
 assert.equal(await page.locator('#settings-dialog').evaluate(el=>el.open),false);
 await page.locator('#keys-button').click();
 assert.ok(await page.locator('#settings-controls').isVisible(),'Hotkey button opens the control tab');
 assert.equal(await page.locator('#browser-status').textContent(),'Подключено','Connected status survives reopening settings');
 assert.equal(await page.locator('#yandex-domain').inputValue(),'music.yandex.kz','Regional choice survives reopening settings');
 assert.equal(await page.locator('#assistant-provider').inputValue(),'gemini','Assistant provider survives reopening settings');
 await page.screenshot({path:'tests/settings-preview.png'});
 await page.keyboard.press('Escape');
 await page.setViewportSize({width:350,height:600});
 const metrics=await page.evaluate(()=>({scroll:document.documentElement.scrollWidth,width:innerWidth,bottom:document.querySelector('.bottom-hint').getBoundingClientRect().bottom,height:innerHeight}));
 assert.equal(metrics.scroll,metrics.width);assert.ok(metrics.bottom<=metrics.height,JSON.stringify(metrics));
 assert.ok(await page.locator('#genres').evaluate(el=>el.scrollHeight>el.clientHeight));
 await page.locator('#settings-button').click();
 await page.locator('#save-shortcuts').scrollIntoViewIfNeeded();
 const saveBounds=await page.locator('#save-shortcuts').boundingBox();
 assert.ok(saveBounds.y>=0 && saveBounds.y+saveBounds.height<=600,'Save remains reachable on short screens');
 for(const name of ['controls','appearance','browser','assistant','island','discord','mood']){
   await page.locator('[data-settings-tab='+name+']').click();
   const bounds=await page.locator('#save-shortcuts').boundingBox();
   assert.ok(bounds.y>=0&&bounds.y+bounds.height<=600,'Save remains visible on '+name);
   const nav=await page.locator('.settings-tabs').boundingBox();
   assert.ok(nav.x>=0&&nav.x+nav.width<=350,'Tabs fit the narrow window');
 }
 await page.screenshot({path:'tests/settings-tabs-preview.png'});
 await page.keyboard.press('Escape');
 await page.evaluate(async()=>{
   window.browserStatus={embedded:true,name:'Встроенный плеер',supported:true,configured:true,connected:true,signed_in:false,state:'sign_in',message:'Войдите в музыкальный аккаунт'};
   await poll();
 });
 await page.locator('#settings-button').click();
 await page.locator('[data-settings-tab=browser]').click();
 assert.equal(await page.locator('#settings-tab-browser').textContent(),'Аккаунт');
 assert.equal(await page.locator('#connect-browser').textContent(),'Войти в Яндекс');
 assert.equal(await page.locator('#connect-browser').isDisabled(),false,'Internal engine readiness does not disable sign-in');
 assert.equal(await page.locator('#browser-persist-shortcuts').isVisible(),false,'No desktop browser shortcuts in embedded mode');
 await page.locator('#connect-browser').click();
 assert.equal(await page.evaluate(()=>window.accountShown),true);
 assert.equal(await page.locator('#connect-browser').textContent(),'Войти в Яндекс','Opening sign-in does not claim the account is authenticated');
 await page.evaluate(async()=>{window.browserStatus={...window.browserStatus,signed_in:true,state:'connected',message:'Аккаунт подключён'};await poll();});
 assert.equal(await page.locator('#connect-browser').textContent(),'Сменить аккаунт');
 assert.equal(await page.locator('#connect-browser').isDisabled(),false);
 assert.ok((await page.locator('#connection-detail').textContent()).includes('Внутри приложения'));
 await page.screenshot({path:'tests/embedded-account-preview.png'});
 await page.keyboard.press('Escape');
 for(const service of ['spotify','soundcloud']){
   await page.locator('[data-service='+service+']').click();
   await page.waitForFunction(service=>document.body.dataset.service===service,service);
   assert.equal(await page.locator('.brand strong').textContent(),service==='spotify'?'Spotify':'SoundCloud');
   assert.ok((await page.locator('.brand-mark').getAttribute('src')).endsWith('/'+service+'.png'),'Header uses the official service logo');
   assert.ok(await page.locator('#service-search').isVisible());
   assert.equal(await page.locator('.genres-section').first().isVisible(),false,'My Wave stays exclusive to Yandex');
   if(service==='spotify'){
     await page.waitForFunction(()=>document.querySelectorAll('.spotify-playlist').length===2);
     assert.ok(await page.locator('#spotify-library').isVisible());
     assert.equal(await page.locator('#add-spotify-playlist').textContent(),'Добавить плейлист');
     await page.locator('#add-spotify-playlist').click();
     await page.locator('#spotify-playlist-url').fill('https://example.com/playlist/0123456789ABCDEFGHIJKL');
     await page.locator('#save-spotify-playlist').click();
     assert.ok(await page.locator('#spotify-playlist-error').isVisible());
     await page.locator('#spotify-playlist-url').fill('https://open.spotify.com/playlist/0123456789ABCDEFGHIJKL?si=shared');
     await page.locator('#save-spotify-playlist').click();
     assert.equal(await page.locator('#spotify-playlist-dialog').isVisible(),false);
     assert.equal(await page.locator('.spotify-playlist').count(),3);
     await page.locator('.spotify-playlist').last().click();
     assert.deepEqual(await page.evaluate(()=>window.calls.at(-1)),['spotify_playlist','https://open.spotify.com/playlist/0123456789ABCDEFGHIJKL']);
     await page.evaluate(async()=>{window.spotifyAccount='account-b';await poll();});
     await page.waitForFunction(()=>document.querySelectorAll('.spotify-playlist').length===0);
     await page.evaluate(async()=>{window.spotifyAccount='account-a';await poll();});
     await page.waitForFunction(()=>document.querySelectorAll('.spotify-playlist').length===3);
     await page.locator('#add-spotify-playlist').click();await page.keyboard.press('Escape');
     assert.equal(await page.locator('#spotify-playlist-dialog').isVisible(),false,'Escape closes import dialog');
   }
   await page.locator('#service-query').fill('Test Artist Test Song');
   await page.locator('#service-search-play').click();
   assert.deepEqual(await page.evaluate(()=>window.calls.at(-1)),['search_play','Test Artist Test Song']);
   await page.locator('#settings-button').click();
   await page.locator('[data-settings-tab=browser]').click();
   assert.ok((await page.locator('#browser-name').textContent()).includes(service==='spotify'?'Spotify':'SoundCloud'));
   assert.equal(await page.locator('#yandex-domain').isVisible(),false);
   await page.keyboard.press('Escape');
   const serviceMetrics=await page.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,bottom:document.querySelector('.bottom-hint').getBoundingClientRect().bottom,height:innerHeight}));
   assert.equal(serviceMetrics.scroll,serviceMetrics.width);assert.ok(serviceMetrics.bottom<=serviceMetrics.height,JSON.stringify(serviceMetrics));
   await page.screenshot({path:'tests/'+service+'-preview.png'});
 }
 assert.deepEqual(errors,[]);
 await browser.close();
 console.log('PASS UI: voice record/cancel/wait/transcript, transport, favorites, dynamic My Wave cards, refresh/error states, 7 hotkeys, 7 settings tabs, embedded sign-in status, keyboard navigation, drafts across tabs, settings save, narrow/short viewport, no JS errors');
})().catch(e=>{console.error(e);process.exit(1);});
