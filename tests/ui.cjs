let playwright;
try { playwright=require('playwright'); } catch (_) { playwright=require(require('node:path').join(require('node:os').homedir(),'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')); }
const {chromium}=playwright;
const assert = require('node:assert/strict');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:path.join(process.env['ProgramFiles(x86)']||'C:/Program Files (x86)','Microsoft/Edge/Application/msedge.exe')});
 const page=await browser.newPage({viewport:{width:414,height:1000},deviceScaleFactor:1});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{
   const bindings=Object.fromEntries([['toggle','Num0'],['next','Num6'],['previous','Num4'],['play_pause','Num5'],['stop','Num2'],['like','Num1'],['voice','Num3']].map(([a,key])=>[a,{key,ctrl:false,alt:false,shift:false}]));
   window.voice={phase:'idle',message:'Скажите: «включи трек Династия»',text:''};
   window.calls=[];
   window.browserStatus={name:'Brave',supported:true,configured:false,connected:false,state:'not_configured',message:'Подключите браузер один раз',shortcuts_updated:false};
   window.liquid={api:{
     get_settings:async()=>({backend:"csharp",browser:window.browserStatus,bindings,actions:{toggle:'Показать / скрыть',next:'Следующий трек',previous:'Предыдущий трек',play_pause:'Пауза / продолжить',stop:'Остановить воспроизведение',like:'Лайк / убрать лайк',voice:'Голосовая команда'},keys:['Num0','Num1','Num2','Num3','Num4','Num5','Num6','F6','F8'],genres:['Хип-хоп','Рок','Электроника','Поп','Инди','Метал','Альтернатива','Джаз','Релакс','Лоу-фай','R&B','Классика','Техно','Ретро'],on_top:true,startup:false}),
     get_state:async()=>({title:'NIGHT DRIVE',artist:'PHARAOH',playing:true,liked:false,connected:true,artwork:'',position:102,duration:197,volume:.68,voice:window.voice,browser:window.browserStatus}),
     start_voice:async()=>{window.voice={phase:'listening',message:'Слушаю…',text:''};return {ok:true};},
     cancel_voice:async()=>{window.voice={phase:'idle',message:'Запись отменена',text:''};return {ok:true};},
     command:async(...args)=>{window.calls.push(args);return {ok:true};},
     setup_browser:async(persist)=>{
       if(window.cancelSetup)return {ok:false,cancelled:true,browser:window.browserStatus};
       if(window.failSetup)return {ok:false,error:'Ошибка подключения',browser:window.browserStatus};
       window.browserStatus={name:'Brave',supported:true,configured:true,connected:true,state:'connected',message:'Подключено',shortcuts_updated:persist};
       return {ok:true,browser:window.browserStatus};
     },
     save_settings:async(b)=>{window.saved=b;return {ok:true};},
     set_yandex_domain:async(domain)=>{window.yandexDomain=domain;return {ok:true};},
     hide:async()=>({ok:true}),quit:async()=>({ok:true}),set_topmost:async()=>({ok:true}),set_startup:async()=>({ok:true})
   }};
 });
 await page.goto(pathToFileURL(path.resolve('ui/index.html')).href);
 await page.waitForFunction(()=>document.getElementById('track-title').textContent==='NIGHT DRIVE');
 assert.equal(await page.locator('.genre').count(),14);
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
 await page.evaluate(()=>document.getElementById('toast').hidden=true);
 await page.screenshot({path:'tests/ui-preview.png'});
 await page.locator('#settings-button').click();
 await page.locator('#save-shortcuts').scrollIntoViewIfNeeded();
 assert.equal(await page.locator('.shortcut-row').count(),7);
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
 await page.locator('#key-next').selectOption('F8');
 await page.locator('#save-shortcuts').click();
 assert.equal(await page.evaluate(()=>window.saved.next.key),'F8');
 assert.equal(await page.locator('#settings-dialog').evaluate(el=>el.open),false);
 await page.locator('#keys-button').click();
 assert.equal(await page.locator('#browser-status').textContent(),'Подключено','Connected status survives reopening settings');
 assert.equal(await page.locator('#yandex-domain').inputValue(),'music.yandex.kz','Regional choice survives reopening settings');
 await page.screenshot({path:'tests/settings-preview.png'});
 await page.keyboard.press('Escape');
 await page.setViewportSize({width:350,height:600});
 const metrics=await page.evaluate(()=>({scroll:document.documentElement.scrollWidth,width:innerWidth,bottom:document.querySelector('.bottom-hint').getBoundingClientRect().bottom,height:innerHeight}));
 assert.equal(metrics.scroll,metrics.width);assert.ok(metrics.bottom<=metrics.height,JSON.stringify(metrics));
 assert.ok(await page.locator('.genres').evaluate(el=>el.scrollHeight>el.clientHeight));
 await page.locator('#settings-button').click();
 await page.locator('#save-shortcuts').scrollIntoViewIfNeeded();
 const saveBounds=await page.locator('#save-shortcuts').boundingBox();
 assert.ok(saveBounds.y>=0 && saveBounds.y+saveBounds.height<=600,'Save remains reachable on short screens');
 assert.deepEqual(errors,[]);
 await browser.close();
 console.log('PASS UI: voice record/cancel/wait/transcript, transport, favorites, 14 genres, 7 hotkeys, settings save, narrow/short viewport, no JS errors');
})().catch(e=>{console.error(e);process.exit(1);});
