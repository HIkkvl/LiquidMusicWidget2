({action, service, command, query, volume, resetFromReturn=false, resetFromButton=false}) => {
  const cyrillic='абвгдеёжзийклмнопрстуфхцчшщъыьэюя';
  const latin=['a','b','v','g','d','e','e','zh','z','i','y','k','l','m','n','o','p','r','s','t','u','f','h','ts','ch','sh','shch','','y','','e','yu','ya'];
  const months={январь:'января',января:'января',февраль:'февраля',февраля:'февраля',март:'марта',марта:'марта',апрель:'апреля',апреля:'апреля',май:'мая',мая:'мая',июнь:'июня',июня:'июня',июль:'июля',июля:'июля',август:'августа',августа:'августа',сентябрь:'сентября',сентября:'сентября',октябрь:'октября',октября:'октября',ноябрь:'ноября',ноября:'ноября',декабрь:'декабря',декабря:'декабря'};
  const days={один:1,одно:1,одного:1,два:2,три:3,четыре:4,пять:5,шесть:6,семь:7,восемь:8,девять:9,десять:10,одиннадцать:11,двенадцать:12,тринадцать:13,четырнадцать:14,пятнадцать:15,шестнадцать:16,семнадцать:17,восемнадцать:18,девятнадцать:19,двадцать:20,тридцать:30};
  const ordinals={перв:1,втор:2,четверт:4,пят:5,шест:6,седьм:7,восьм:8,девят:9,десят:10,одиннадцат:11,двенадцат:12,тринадцат:13,четырнадцат:14,пятнадцат:15,шестнадцат:16,семнадцат:17,восемнадцат:18,девятнадцат:19,двадцат:20,тридцат:30};
  const day=word=>{
    const numeric=/^(\d{1,2})(?:го|й|е|ое|ый|ого)?$/.exec(word);
    if(numeric&&+numeric[1]>=1&&+numeric[1]<=31)return +numeric[1];
    if(Object.hasOwn(days,word))return days[word];
    if(/^трет(?:ий|ье|ьего|ьему|ьим|ьем|ья|ью|ьей)$/.test(word))return 3;
    const ordinal=/^(.+?)(?:ый|ой|ое|ого|ому|ым|ом|ая|ую)$/.exec(word);
    return ordinal&&Object.hasOwn(ordinals,ordinal[1])?ordinals[ordinal[1]]:0;
  };
  const norm = text => {
    const words=String(text||'').toLowerCase().replace(/ё/g,'е').replace(/[^\p{L}\p{N}]+/gu,' ').trim().split(/\s+/);
    for(let i=1;i<words.length;i++){
      if(!Object.hasOwn(months,words[i]))continue;
      let number=day(words[i-1]);
      if(!number&&i>=2&&['го','й','е'].includes(words[i-1])){number=day(words[i-2]);if(number)words[i-2]='';}
      if(!number)continue;
      if(i>=2&&[20,30].includes(day(words[i-2]))&&number<10){const combined=day(words[i-2])+number;if(combined>31)continue;number=combined;words[i-2]='';}
      words[i-1]=String(number);words[i]=months[words[i]];
    }
    return words.join(' ').replace(/[а-яё]/g,c=>latin[cyrillic.indexOf(c)]).replace(/[^\p{L}\p{N}]+/gu,' ').trim();
  };
  const text = selectors => {
    for (const selector of selectors) {
      const el=document.querySelector(selector);
      if(el?.textContent?.trim())return el.textContent.trim();
    }
    return '';
  };
  const playerSelector=service==='spotify'?'[data-testid="now-playing-bar"],.Root__now-playing-bar':service==='soundcloud'?'.playControls':'[class*="PlayerBar"],ytmusic-player-bar,#movie_player';
  const players=[...document.querySelectorAll(playerSelector)];
  // Яндекс Музыка создаёт `new Audio()` и не кладёт его в DOM: такой элемент не найти через querySelectorAll.
  // Ловим его при play() и принимаем список, который приложение собрало через CDP (Runtime.queryObjects).
  if(service!=='youtube'&&!window.__lmHook){
    window.__lmHook=true;window.__lmMedia=window.__lmMedia||[];
    const nativePlay=HTMLMediaElement.prototype.play;
    HTMLMediaElement.prototype.play=function(...args){
      const list=window.__lmMedia;if(!list.includes(this)){list.push(this);if(list.length>20)list.shift();}
      return nativePlay.apply(this,args);
    };
    // Плеер сайта постоянно читает currentTime/paused/duration (рисует свой прогресс): по первому же чтению запоминаем элемент,
    // даже если он создан до нашего хука и не лежит в DOM. Работает без перекомпиляции приложения.
    for(const prop of ['currentTime','duration','paused']){
      const descriptor=Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype,prop);
      if(!descriptor||!descriptor.get)continue;
      Object.defineProperty(HTMLMediaElement.prototype,prop,{...descriptor,get(){
        const list=window.__lmMedia;
        if(list&&this instanceof HTMLMediaElement&&!list.includes(this)){list.push(this);if(list.length>20)list.shift();}
        return descriptor.get.call(this);
      }});
    }
    // Сайт сообщает системе позицию/длительность через Media Session — запоминаем её
    const sessionProto=window.MediaSession&&MediaSession.prototype;
    if(sessionProto&&sessionProto.setPositionState){
      const nativeState=sessionProto.setPositionState;
      sessionProto.setPositionState=function(state){
        try{if(state&&Number.isFinite(state.duration)&&state.duration>0)window.__lmPos={duration:state.duration,position:Number(state.position)||0,rate:Number(state.playbackRate)||1,at:performance.now()};}catch(_){}
        return nativeState.apply(this,arguments);
      };
    }
  }
  const realMedia=el=>{try{return el instanceof HTMLMediaElement&&typeof el.paused==='boolean';}catch(_){return false;}};   // прототипы (HTMLAudioElement.prototype) бросают Illegal invocation
  const registered=(window.__lmMedia||[]).filter(realMedia);
  const medias=[...new Set([...document.querySelectorAll('audio,video'),...registered])];
  const usable=el=>el.currentTime>0&&Number.isFinite(el.duration)&&el.duration>0;
  // Preserve main-player selection: preview videos are not Yandex's audio engine.
  const playbackMedia=service==='yandex'?medias.filter(el=>el.tagName==='AUDIO'):medias;
  const media=playbackMedia.find(el=>!el.paused)||[...playbackMedia].reverse().find(usable)||playbackMedia[playbackMedia.length-1];
  const metadata=navigator.mediaSession?.metadata;
  const label=el=>[el.getAttribute('aria-label'),el.getAttribute('title')].filter(Boolean).join(' ');
  const enabled=el=>!el.hasAttribute('disabled') && el.getAttribute('aria-disabled')!=='true' && el.getAttribute('data-disabled')!=='true'&&!el.classList.contains('disabled');
  const visible=el=>{
    if(!el||!el.getClientRects().length||getComputedStyle(el).visibility==='hidden'||getComputedStyle(el).display==='none')return false;
    const rect=el.getBoundingClientRect();return rect.right>0&&rect.bottom>0&&rect.left<innerWidth&&rect.top<innerHeight;
  };
  const spotifyControl=name=>{
    const selector='[data-testid="'+name+'"]';
    const controls=[...document.querySelectorAll(selector)].filter(visible);
    // Spotify renders extra transport controls for compact/fullscreen views.
    // Resolve the visible control in the actual player instead of the first.
    const candidates=controls.filter(enabled);
    return candidates.find(el=>{
      const rect=el.getBoundingClientRect(),hit=document.elementFromPoint(rect.left+rect.width/2,rect.top+rect.height/2);
      return hit&&(hit===el||el.contains(hit));
    })||candidates.find(el=>el.closest(playerSelector))||candidates[0]||controls[0]||null;
  };
  const buttons=players.flatMap(root=>[...root.querySelectorAll('button,[role="button"],.ytp-next-button,.ytp-prev-button')]);
  const playPattern=/^(?:воспроизведение|воспроизвести|включить|слушать|продолжить|играть|play|resume|ойнату)(?:\s|$)/i;
  const hasPause=buttons.some(el=>enabled(el)&&/^(?:пауза|pause|кідірту)(?:\s|$)/i.test(label(el)));
  const hasPlay=buttons.some(el=>enabled(el)&&playPattern.test(label(el)));
  // Yandex can play the main track through its internal engine while a paused,
  // muted preview <video> remains in the document. Its state is not the player.
  const spotifyTransport=service==='spotify'?spotifyControl('control-button-playpause'):null;
  const spotifyPaused=spotifyTransport&&playPattern.test(label(spotifyTransport));
  const spotifyPlaying=spotifyTransport&&/^(?:пауза|pause|кідірту)(?:\s|$)/i.test(label(spotifyTransport));
  const sessionState=navigator.mediaSession?.playbackState;
  const playing=service==='spotify'?(spotifyPlaying?true:spotifyPaused?false:sessionState==='playing'?true:sessionState==='paused'?false:!!media&&!media.paused):service==='yandex'?(hasPause?true:hasPlay?false:sessionState==='playing'||!!media&&!media.paused):media?!media.paused:sessionState==='playing'||hasPause;
  if(service==='spotify'&&action==='spotify_media_command')return (async()=>{
    const actionName={pause:'pause',stop:'pause',play:'play',play_pause:playing?'pause':'play',next:'nexttrack',previous:'previoustrack'}[command];
    const handler=window.__liquidMediaActions?.[actionName];
    if(!handler)return {ok:false};
    try{await handler({action:actionName});return {ok:true};}catch{return {ok:false};}
  })();
  const patterns={
    next:/следующ|next|келесі|^далее$/i, previous:/предыдущ|previous|алдыңғы|^назад$/i,
    pause:/пауза|pause|кідірту/i,play:playPattern,
    like:/мне нравится|понравивш|нравится|\blike\b|liked|unlike|ұнайды|медиатек|любимые треки|save.*library|remove.*library|add.*library/i
  };
  const volumeInput=document.querySelector('[data-testid="volume-bar"] input[type="range"],input[type="range"][aria-label*="громк" i],input[type="range"][aria-label*="volume" i]');
  const spotifyAvatar=document.querySelector('[data-testid="user-widget-link"],[data-testid="user-widget-avatar"]');
  if(service==='spotify')window.__liquidSpotifyDocument||=crypto.randomUUID();
  const spotifyHint=spotifyAvatar?[label(spotifyAvatar),spotifyAvatar.textContent,spotifyAvatar.querySelector('img')?.getAttribute('src')].join('|'):'';
  if(window.__liquidSpotifyAccount?.hint!==spotifyHint)delete window.__liquidSpotifyAccount;
  if(window.__liquidSpotifyPlaylist&&spotifyHint){
    if(!window.__liquidSpotifyPlaylist.hint)window.__liquidSpotifyPlaylist.hint=spotifyHint;
    else if(window.__liquidSpotifyPlaylist.hint!==spotifyHint)delete window.__liquidSpotifyPlaylist;
  }
  const playlistUrl=value=>{
    try{const url=new URL(value,location.href);const match=/^\/(?:intl-[a-zA-Z-]+\/)?playlist\/([a-zA-Z0-9]{22})\/?$/.exec(url.pathname);
      return url.origin==='https://open.spotify.com'&&match?'https://open.spotify.com/playlist/'+match[1]:null;
    }catch{return null;}
  };
  if(service==='spotify'&&action==='spotify_account'){
    if(window.__liquidSpotifyAccount?.account)return {account:window.__liquidSpotifyAccount.account,can_open:!!spotifyAvatar};
    const links=[...document.querySelectorAll('[role="menu"] a[href*="/user/"],[data-testid="user-widget-dropdown"] a[href*="/user/"]')];
    const profile=links.find(link=>link.getClientRects().length&&getComputedStyle(link).visibility!=='hidden');
    if(profile){const url=new URL(profile.href,location.href);const match=/^\/(?:intl-[a-zA-Z-]+\/)?user\/([^/]+)\/?$/.exec(url.pathname);
      if(url.origin==='https://open.spotify.com'&&match){const account=decodeURIComponent(match[1]);window.__liquidSpotifyAccount={account,hint:spotifyHint};return {account,can_open:true};}}
    if(spotifyAvatar)spotifyAvatar.setAttribute('data-liquid-control','spotify-account');
    return {can_open:!!spotifyAvatar};
  }
  if(service==='spotify'&&action==='spotify_library'){
    const result=[],seen=new Set();
    for(const link of document.querySelectorAll('a[href*="/playlist/"]')){
      const url=playlistUrl(link.href);if(!url||seen.has(url))continue;
      if(!link.getClientRects().length||getComputedStyle(link).visibility==='hidden')continue;
      let card=link;
      for(let parent=link.parentElement,depth=0;parent&&depth<5;parent=parent.parentElement,depth++){
        const urls=new Set([...parent.querySelectorAll('a[href*="/playlist/"]')].map(a=>playlistUrl(a.href)).filter(Boolean));
        if(urls.size>1)break;card=parent;if(parent.querySelector('img'))break;
      }
      const title=(link.getAttribute('aria-label')||link.innerText||card.querySelector('[data-testid="card-title"],[data-testid="entityTitle"]')?.textContent||card.innerText||'').replace(/\s+/g,' ').trim();
      if(!title||title.length>200)continue;
      const image=card.querySelector('img');seen.add(url);
      result.push({id:url.split('/').at(-1),url,title,artwork:image?.currentSrc||image?.src||''});
    }
    return result;
  }
  if(service==='spotify'&&action==='spotify_home_target'){
    const home=[...document.querySelectorAll('button,a')].find(el=>/^(?:главная|home)$/i.test(label(el).trim())&&enabled(el));
    if(home)home.setAttribute('data-liquid-control','spotify-home');return !!home;
  }
  if(service==='spotify'&&action==='spotify_library_scroll'){
    const sidebar=document.getElementById('Desktop_LeftSidebar_Id');
    const scroll=sidebar&&[...sidebar.querySelectorAll('*')].filter(el=>el.clientHeight>60&&el.scrollHeight>el.clientHeight+8&&/auto|scroll/.test(getComputedStyle(el).overflowY)).sort((a,b)=>b.scrollHeight-a.scrollHeight)[0];
    if(!scroll)return false;
    if(!window.__liquidSpotifyScroll)window.__liquidSpotifyScroll={element:scroll,top:scroll.scrollTop};
    const before=scroll.scrollTop;scroll.scrollTop+=Math.max(200,scroll.clientHeight*.8);return scroll.scrollTop>before;
  }
  if(service==='spotify'&&action==='spotify_library_restore'){
    const saved=window.__liquidSpotifyScroll;if(saved?.element?.isConnected)saved.element.scrollTop=saved.top;
    delete window.__liquidSpotifyScroll;return {ok:true};
  }
  if(service==='spotify'&&action==='spotify_playlist_target'){
    document.querySelectorAll('[data-liquid-control="spotify-playlist"]').forEach(el=>el.removeAttribute('data-liquid-control'));
    const controls=[...document.querySelectorAll('main button[data-testid="play-button"],[role="main"] button[data-testid="play-button"],[data-testid="main-view"] button[data-testid="play-button"]')];
    const button=controls.find(el=>enabled(el)&&!el.closest('[data-testid="tracklist-row"],[data-testid="card"]'));
    if(button&&/^(?:пауза|pause)(?:\s|$)/i.test(label(button))&&playing)return {found:true,already:true};
    if(button)button.setAttribute('data-liquid-control','spotify-playlist');return {found:!!button};
  }
  if(service==='spotify'&&action==='spotify_playlist_selected'){window.__liquidSpotifyPlaylist=query?{...query,hint:spotifyHint}:null;return {ok:true};}
  if(service==='spotify'&&action==='spotify_playlist_details'){
    const main=document.querySelector('main,[role="main"],[data-testid="main-view"]');
    const heading=main?.querySelector('h1,[data-encore-id="text"][as="h1"]');
    const image=main?.querySelector('[data-testid="playlist-image"] img,img[data-testid="playlist-image"],header img');
    return {title:heading?.textContent?.trim()||'',artwork:image?.currentSrc||image?.src||''};
  }
  // Read only rendered controls belonging to My Wave, never search results or account storage.
  const waveOptions=()=>{
    if(service!=='yandex')return [];
    const wheels=[...document.querySelectorAll('[aria-label="Рекомендации моей волны"],[class*="WheelDesktop_root"],[class*="WheelMobile_root"]')];
    const roots=wheels.length?wheels:[...document.querySelectorAll('[class*="VibeBlock_root"],[class*="VibePage_root"],[aria-label="Моя волна"]')];
    const interactive='button,[role="button"],a,[tabindex="0"]';
    const card=el=>String(el.className).split(/\s+/).some(name=>/(?:item|card|slide|option|pin)(?:_|$)/i.test(name)&&!/(?:^|_)(?:list|items|cards|container|content|title|image|icon|cover|text)(?:_|$)/i.test(name));
    const candidates=new Set();
    for(const root of roots){
      if(wheels.length){for(const el of root.querySelectorAll('[class*="WheelItem_root"]'))candidates.add(el);continue;}
      if(root.matches(interactive)||card(root))candidates.add(root);
      for(const el of root.querySelectorAll(interactive+',[class*="item" i],[class*="card" i],[class*="slide" i],[class*="option" i],[class*="pin" i]')){
        if(el.matches(interactive)||card(el))candidates.add(el);
      }
    }
    const seen=new Set(),result=[];
    for(const el of candidates){
      if(el.closest('nav,aside,'+playerSelector)||el.hidden||getComputedStyle(el).display==='none'||getComputedStyle(el).visibility==='hidden')continue;
      // Use the outer clickable card when its children repeat the same title.
      let nested=false;
      for(let parent=el.parentElement;parent;parent=parent.parentElement){if(candidates.has(parent)&&card(parent)){nested=true;break;}}
      if(nested)continue;
      const wheelTitle=el.querySelector('[class*="WheelItem_title"]')?.textContent;
      const description=el.querySelector('[class*="WheelItem_description"]')?.textContent;
      const title=(wheelTitle?[description,wheelTitle].filter(Boolean).join(' '):el.innerText?.trim()||el.getAttribute('aria-label')||el.querySelector('img')?.alt||'').replace(/\s+/g,' ').trim();
      if(!title||title.length>100||/^(?:моя волна|воспроизведение|воспроизвести|слушать|пауза|настроить|настройки|войти|play|pause|settings|закрыть|далее|назад)(?:$|[ :])/i.test(title)||/^Назад\s+к\s+привычному$/i.test(title)||seen.has(title))continue;
      const rect=el.getBoundingClientRect();if(!rect.width||!rect.height)continue;
      const image=el.querySelector('img');
      const artwork=image?.currentSrc||image?.src||'';
      const control=el.matches(interactive)?el:[...el.querySelectorAll('button,[role="button"]')].find(button=>/^(?:воспроизведение|воспроизвести|слушать|пауза|play|pause)(?:$|\s)/i.test(label(button)))||el;
      if(wheels.length&&control===el&&!/^Встряхнуть/i.test(title))continue;
      seen.add(title);result.push({id:title,title,artwork:/^https:\/\//i.test(artwork)?artwork:'',disabled:!enabled(el)||!enabled(control),element:control,container:el});
      if(result.length>=60)break;
    }
    return result;
  };
  if(action==='wave_list')return waveOptions().map(({element,container,...item})=>item);
  const pausedWave=item=>/^(?:пауза|pause)(?:$|\s)/i.test(label(item.element));
  const defaultWaveControl=()=>[...document.querySelectorAll('button,[role="button"]')].filter(el=>
    !el.closest('nav,aside,[class*="Wheel"],'+playerSelector)&&enabled(el)&&el.getClientRects().length
  ).find(el=>/^(?:воспроизведение|воспроизвести|слушать|продолжить|пауза)\s+(?:моей волны|мою волну|моя волна)$/i.test(label(el).trim())||/^Моя волна$/i.test(el.innerText?.trim()||''));
  const returnWaveCard=()=>{
    const cards=[...document.querySelectorAll('[class*="WheelItem_root"]')].filter(el=>enabled(el)&&el.getClientRects().length&&getComputedStyle(el).visibility!=='hidden'&&
      /^Назад\s+к\s+привычному$/i.test((el.getAttribute('aria-label')||el.textContent||'').replace(/\s+/g,' ').trim()));
    return cards.find(visible)||cards[0]||null;
  };
  const resetWaveButton=()=>service==='yandex'?[...document.querySelectorAll('button[class*="VibeResetButton_button"],button[aria-label^="Сбросить "]')].find(el=>
    enabled(el)&&visible(el)&&!!el.closest('[class*="VibePage_root"],[class*="VibeBlock_root"]')&&/и\s+включить\s+Мою\s+волну\s*$/i.test(el.getAttribute('aria-label')||''))||null:null;
  const resetWaveTitle=el=>el?.querySelector('[class*="VibeResetButton_context"]')?.textContent?.trim()||el?.textContent?.trim()||'';
  const waveByContext=(items,title)=>{
    const context=norm(title);if(!context)return null;
    const exact=items.find(item=>norm(item.title)===context);if(exact)return exact;
    const matches=items.filter(item=>norm(item.title).startsWith(context+' ')||norm(item.title).endsWith(' '+context));
    return matches.length===1?matches[0]:null;
  };
  const revealWaveCard=container=>{
    const dialog=[...document.querySelectorAll('dialog[open],[role="dialog"][aria-modal="true"]')].some(el=>el.getClientRects().length&&getComputedStyle(el).visibility!=='hidden');
    if(dialog)return {found:true,blocked:true};
    const slide=container.closest('[data-swiper-slide-index],.swiper-slide');
    const swiper=slide?.closest('.swiper')?.swiper;
    if(swiper&&typeof swiper.slideTo==='function'){
      const index=Array.from(swiper.slides||[]).indexOf(slide);
      if(index>=0&&swiper.activeIndex!==index)swiper.slideTo(index,0,true);
    }
    return {found:true};
  };
  if(action==='wave_default_reveal'){
    const control=resetWaveButton()||returnWaveCard();return control?revealWaveCard(control):{found:false};
  }
  if(action==='wave_selection'){window.__liquidWaveSelection=query||null;return {ok:true};}
  if(action==='wave_default_target'||action==='wave_default_status'){
    const reset=resetWaveButton();
    const returning=reset||returnWaveCard();
    const control=returning||defaultWaveControl();
    const active=!!reset||waveOptions().some(pausedWave);
    const resetCompleted=(resetFromButton?!reset:resetFromReturn&&!returning)&&!active&&!!document.querySelector('[class*="VibePage_root"],[class*="VibeBlock_root"]');
    const modernRoot=document.querySelector('[class*="VibePage_bigCardPanel"]');
    const modernDefault=!!modernRoot&&!/VibePage_root_reshuffle/.test(String(modernRoot.className))&&!window.__liquidWaveSelection&&!active;
    const isDefault=resetCompleted||modernDefault||!returning&&!!control&&/^(?:пауза|pause)(?:$|\s)/i.test(label(control))&&!active;
    if(action==='wave_default_target'){
      document.querySelectorAll('[data-liquid-control],[data-liquid-wave-card]').forEach(el=>{el.removeAttribute('data-liquid-control');el.removeAttribute('data-liquid-wave-card');});
      if(control&&!isDefault)control.setAttribute('data-liquid-control','wave-default');
      if(returning)returning.setAttribute('data-liquid-wave-card','true');
    }
    return {found:!!control,playing:playing&&isDefault,active,returning:!!returning,reset_kind:reset?'button':returning?'card':null,default:isDefault};
  }
  if(action==='wave_status'){
    const items=waveOptions(),item=items.find(item=>item.id===query);
    const selected=waveByContext(items,resetWaveTitle(resetWaveButton()));
    return {found:!!item,playing:!!item&&(/^(?:пауза|pause)(?:$|\s)/i.test(label(item.element))||playing&&selected?.id===query)};
  }
  if(action==='wave_reveal'){
    const item=waveOptions().find(item=>item.id===query&&!item.disabled);
    if(!item)return {found:false};
    return revealWaveCard(item.container);
  }
  if(action==='wave_target'){
    document.querySelectorAll('[data-liquid-control],[data-liquid-wave-card]').forEach(el=>{el.removeAttribute('data-liquid-control');el.removeAttribute('data-liquid-wave-card');});
    const item=waveOptions().find(item=>item.id===query&&!item.disabled);
    if(!item)return {found:false};
    item.element.setAttribute('data-liquid-control','wave');item.container.setAttribute('data-liquid-wave-card','true');
    return {found:true};
  }
  if(action==='wave_home_target'){
    document.querySelectorAll('[data-liquid-control]').forEach(el=>el.removeAttribute('data-liquid-control'));
    const links=[...document.querySelectorAll('a[href]')].filter(el=>{
      const url=new URL(el.href,location.href);
      return url.origin===location.origin&&(/^(?:Моя волна|Главная)$/i.test((el.innerText||el.getAttribute('aria-label')||'').trim())||url.pathname==='/'&&!!el.closest('nav,[class*="Sidebar"],[class*="Navigation"]'));
    });
    const home=links.find(el=>/Моя волна/i.test(el.innerText||el.getAttribute('aria-label')||''))||links[0];
    if(!home)return false;
    home.setAttribute('data-liquid-control','wave-home');return true;
  }
  // «12:34» / «-0:45» / «1:02:03» -> секунды
  const clockText=value=>{
    const m=String(value||'').trim().match(/^([−–-])?(\d{1,2}):(\d{2})(?::(\d{2}))?$/);
    if(!m)return null;
    const parts=[m[2],m[3],m[4]].filter(v=>v!==undefined).map(Number);
    return {seconds:parts.length===3?parts[0]*3600+parts[1]*60+parts[2]:parts[0]*60+parts[1],negative:!!m[1]};
  };
  const shown=el=>el.getClientRects().length>0;
  // Время из самого плеера: Яндекс Музыка отдаёт звук через MediaSource, и у <audio> duration бывает Infinity/0.
  // Все «12:34»-метки внутри корня по текстовым узлам (видимые), в порядке документа
  const clockTokens=root=>{
    const out=[],walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT);
    for(let node=walker.nextNode();node;node=walker.nextNode()){
      const parent=node.parentElement;if(!parent||!shown(parent))continue;
      const content=node.nodeValue.trim();if(!content||content.length>40)continue;
      for(const m of content.matchAll(/[−–-]?\d{1,2}:\d{2}(?::\d{2})?/g)){const t=clockText(m[0]);if(t)out.push({...t,raw:m[0],cls:String(parent.className||'').slice(0,60)});}
    }
    return out;
  };
  const timeFromPlayer=()=>{
    const pos=window.__lmPos;                         // из Media Session
    if(pos&&pos.duration>0){
      const elapsed=playing?(performance.now()-pos.at)/1000*pos.rate:0;
      return {position:Math.min(pos.duration,pos.position+elapsed),duration:pos.duration,source:'mediasession'};
    }
    const roots=players.length?players:[document.body];
    for(const root of roots){
      const stamps=clockTokens(root);
      if(stamps.length>=2){
        const [current,total]=stamps;
        const duration=total.negative||total.seconds<current.seconds?current.seconds+total.seconds:total.seconds;   // «-2:10» или остаток без знака
        if(duration>0)return {position:current.seconds,duration,source:'text'};
      }
    }
    for(const root of roots){                       // ползунок прогресса в секундах (не громкость)
      for(const slider of root.querySelectorAll('input[type="range"],[role="slider"]')){
        if(/громк|volume/i.test(label(slider)+' '+slider.className))continue;
        const now=Number(slider.getAttribute('aria-valuenow')??slider.value),max=Number(slider.getAttribute('aria-valuemax')??slider.max);
        if(Number.isFinite(now)&&Number.isFinite(max)&&max>=10&&now<=max&&max!==100)return {position:now,duration:max,source:'slider'};
      }
    }
    return null;
  };
  // Дамп для разбора, если времени нет: что на странице есть на самом деле
  if(action==='diagnose_time'){
    const describe=el=>{try{return {tag:el.tagName,paused:el.paused,t:el.currentTime,d:el.duration,ready:el.readyState,src:(el.currentSrc||el.src||'').slice(0,20),inDom:el.isConnected};}catch(_){return {error:'proto'};}};
    return {
      url:location.origin+location.pathname,hooked:!!window.__lmHook,positionState:window.__lmPos||null,
      mediaSession:{metadata:!!navigator.mediaSession?.metadata,state:navigator.mediaSession?.playbackState||''},
      media:medias.map(describe),playersFound:players.length,iframes:document.querySelectorAll('iframe').length,
      playerText:players.slice(0,2).map(el=>(el.innerText||'').slice(0,300)),
      clocks:(players.length?players:[document.body]).slice(0,2).flatMap(root=>clockTokens(root)).slice(0,12),
      sliders:[...document.querySelectorAll('input[type="range"],[role="slider"]')].slice(0,6).map(el=>({label:label(el),cls:String(el.className).slice(0,60),now:el.getAttribute('aria-valuenow')??el.value,max:el.getAttribute('aria-valuemax')??el.max})),
      audioContexts:typeof AudioContext!=='undefined'
    };
  }
  if(action==='snapshot') {
    const options=waveOptions();
    const selectedWave=options.find(pausedWave);
    const reset=resetWaveButton();
    if(reset){
      window.__liquidWaveHasResetButton=true;
      const title=resetWaveTitle(reset);
      window.__liquidWaveSelection||=waveByContext(options,title)?.id||title||null;
    }else if(window.__liquidWaveHasResetButton){window.__liquidWaveHasResetButton=false;window.__liquidWaveSelection=null;}
    if(selectedWave)window.__liquidWaveSelection=selectedWave.id;
    else if(!reset&&defaultWaveControl()&&/^(?:пауза|pause)(?:$|\s)/i.test(label(defaultWaveControl())))window.__liquidWaveSelection=null;
    const waveItems=options.map(({element,container,...item})=>item);
    const signedIn=service==='yandex'?!!document.querySelector('[class*="UserProfile_userName"]')||waveItems.length>0:
      service==='spotify'?!!document.querySelector('[data-testid="user-widget-link"],[data-testid="user-widget-avatar"]'):
      service==='soundcloud'?!!document.querySelector('.header__userNavUsernameButton,.header__userNavAvatar'):
      !!document.querySelector('ytmusic-user-avatar,button#avatar-btn');
    const image=players.flatMap(root=>[...root.querySelectorAll('img')])[0];
    const trackLink=players.flatMap(root=>[...root.querySelectorAll('.playbackSoundBadge__titleLink,a[href*="/track/"],a[href*="/tracks/"],a[href*="watch?v="]')])[0]
      ||(/\/track\/\d+/.test(location.pathname)?{href:location.href}:null)
      ||players.flatMap(root=>[...root.querySelectorAll('a[href*="/album/"]')])[0];
    const streamUrl=media?.currentSrc||media?.src||'';
    let position=Number.isFinite(media?.currentTime)?media.currentTime:0,duration=Number.isFinite(media?.duration)?media.duration:0,timeSource=duration>0?'media':'none';
    if(!(duration>0)){const fallback=timeFromPlayer();if(fallback){position=fallback.position;duration=fallback.duration;timeSource=fallback.source;}}
    const like=document.querySelector(service==='soundcloud'?'.playbackSoundBadge .sc-button-like':service==='spotify'?'[data-testid="now-playing-widget"] [data-testid="add-button"]':'#segmented-like-button button,ytd-segmented-like-dislike-button-renderer like-button-view-model button')||buttons.find(el=>patterns.like.test(label(el))&&!/не нравится|dislike|ұнамайды/i.test(label(el)));
    const badgeArt=document.querySelector('.playbackSoundBadge__avatar .sc-artwork,.playbackSoundBadge__avatar');
    const backgroundArt=badgeArt?getComputedStyle(badgeArt).backgroundImage.match(/url\(["']?(.*?)["']?\)/)?.[1]:'';
    return {
      connected:true,service,signed_in:signedIn,wave_origin:location.origin,waves:waveItems,
      active_wave:service==='yandex'?window.__liquidWaveSelection||null:null,
      spotify_account:service==='spotify'?window.__liquidSpotifyAccount?.account||null:null,
      spotify_account_hint:service==='spotify'?spotifyHint:null,
      spotify_document:service==='spotify'?window.__liquidSpotifyDocument:null,
      active_spotify_playlist:service==='spotify'?window.__liquidSpotifyPlaylist?.id||null:null,
      spotify_playlist_title:service==='spotify'?window.__liquidSpotifyPlaylist?.title||'':null,
      title:metadata?.title||text(['[data-testid="context-item-info-title"]','.playbackSoundBadge__titleLink','[class*="PlayerBar"] [class*="Meta_title"]','[class*="PlayerBar"] [class*="trackTitle"]','[class*="PlayerBar"] [class*="title"]','ytmusic-player-bar .title','ytd-watch-metadata h1'])||'В ожидании музыки',
      artist:metadata?.artist||text(['[data-testid="context-item-info-artist"]','.playbackSoundBadge__lightLink','[class*="PlayerBar"] [class*="Meta_artists"]','[class*="PlayerBar"] [class*="artists"]','ytmusic-player-bar .byline','ytd-video-owner-renderer #channel-name'])||'',
      album:metadata?.album||'',
      artwork:metadata?.artwork?.at(-1)?.src||image?.currentSrc||image?.src||backgroundArt||'',
      track_url:trackLink?new URL(trackLink.href,location.href).href:'',page_url:location.href,
      stream_url:/^https?:\/\//i.test(streamUrl)?streamUrl:'',user_agent:navigator.userAgent,
      playing,liked:like?.getAttribute('aria-pressed')==='true'||like?.getAttribute('aria-checked')==='true'||like?.classList.contains('sc-button-selected')||/убрать|unlike|remove from|удалить.*медиатек/i.test(label(like||document.createElement('i'))),
      media_found:!!media,media_stale:!!(media&&media.paused&&hasPause),
      position,duration,time_source:timeSource,stream_kind:/^https?:/i.test(streamUrl)?'direct':streamUrl.startsWith('blob:')?'blob':'none',
      volume:volumeInput?(Number(volumeInput.value)-Number(volumeInput.min||0))/(Number(volumeInput.max||100)-Number(volumeInput.min||0)):media?.volume??(service==='soundcloud'?Number(document.querySelector('.volume__sliderWrapper')?.getAttribute('aria-valuenow')??1):null),error:''
    };
  }
  document.querySelectorAll('[data-liquid-control]').forEach(el=>el.removeAttribute('data-liquid-control'));
  if(action==='volume') {
    if(volumeInput){const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;setter.call(volumeInput,String(Number(volumeInput.min||0)+volume*(Number(volumeInput.max||100)-Number(volumeInput.min||0))));volumeInput.dispatchEvent(new Event('input',{bubbles:true}));volumeInput.dispatchEvent(new Event('change',{bubbles:true}));return {ok:true};}
    if(media){media.volume=volume;return {ok:true};}
    return {error:'На сайте пока не найден регулятор громкости'};
  }
  if(action==='target') {
    if((command==='pause'||command==='stop')&&!playing&&(service!=='spotify'||spotifyPaused||sessionState==='paused'))return {already:true};
    if(command==='play'&&playing)return {already:true};
    let target=command==='stop'?'pause':command==='play_pause'?(playing?'pause':'play'):command;
    let button;
    if(service==='youtube') {
      const selector=target==='next'?'.ytp-next-button':target==='previous'?'.ytp-prev-button':target==='like'?'#segmented-like-button button,ytd-segmented-like-dislike-button-renderer like-button-view-model button,ytmusic-like-button-renderer button':'.ytp-play-button,ytmusic-player-bar #play-pause-button';
      button=document.querySelector(selector);
    }
    if(service==='spotify')button=target==='like'?[...document.querySelectorAll('[data-testid="now-playing-widget"] [data-testid="add-button"]')].find(el=>visible(el)&&enabled(el)):spotifyControl(target==='next'?'control-button-skip-forward':target==='previous'?'control-button-skip-back':'control-button-playpause');
    if(service==='soundcloud')button=document.querySelector(target==='next'?'.playControls__next':target==='previous'?'.playControls__prev':target==='like'?'.playbackSoundBadge .sc-button-like':'.playControls__play');
    button ||= buttons.find(el=>enabled(el)&&patterns[target]?.test(label(el))&&!(target==='like'&&/не нравится|dislike/i.test(label(el))));
    if(!button||!enabled(button))return {found:false};
    button.setAttribute('data-liquid-control','target');return {found:true};
  }
  if(action==='search_target'||action==='search_click'||action==='search_match') {
    if(service==='spotify'||service==='soundcloud'){
      const words=norm(query).split(' ').filter(Boolean);if(!words.length)return false;
      const rows=document.querySelectorAll(service==='spotify'?'[data-testid="tracklist-row"]':'.soundList__item,.searchList__item .sound,.searchItem .sound');
      for(const row of rows){
        const fragments=[],walker=document.createTreeWalker(row,NodeFilter.SHOW_TEXT);
        for(let node=walker.nextNode();node;node=walker.nextNode())fragments.push(node.nodeValue);
        const full=' '+norm(fragments.join(' ')+' '+(row.getAttribute('aria-label')||''))+' ';
        if(!words.every(word=>full.includes(' '+word+' ')))continue;
        if(action==='search_match')return true;
        const button=[...row.querySelectorAll('button,[role="button"]')].find(el=>enabled(el)&&(playPattern.test(label(el))||el.classList.contains('sc-button-play')));
        if(!button)continue;
        row.setAttribute('data-liquid-control','search-card');button.setAttribute('data-liquid-control','search');return true;
      }
      return false;
    }
    const links=[...document.querySelectorAll('a[href*="/track/"],a[href*="/tracks/"]')].filter(el=>!el.closest(playerSelector));
    const words=norm(query).split(' ').filter(Boolean);
    if(!words.length)return false;
    const seen=new Set();
    for(const link of links) {
      let row=link.parentElement;
      for(let depth=0;row&&row!==document.body&&depth<8;depth++,row=row.parentElement) {
        const paths=new Set([...row.querySelectorAll('a[href*="/track/"],a[href*="/tracks/"]')].map(el=>new URL(el.href,location.origin).pathname));
        if(paths.size>1)break;
        if(seen.has(row))continue;seen.add(row);
        const artistNames=[...row.querySelectorAll('a[href*="/artist/"]')].map(el=>el.textContent).join(' ');
        const full=' '+norm(link.textContent+' '+artistNames+' '+row.innerText+' '+row.getAttribute('aria-label'))+' ';
        if(!words.every(word=>full.includes(' '+word+' ')))continue;
        if(action==='search_match')return true;
        const button=[...row.querySelectorAll('button,[role="button"]')].find(el=>{
          if(!enabled(el)||/пауза|pause|нравится|like|меню|menu/i.test(label(el)))return false;
          if(patterns.play.test(label(el)))return true;
          const info=el.className+' '+el.getAttribute('data-testid')+' '+[...el.querySelectorAll('use')].map(use=>use.getAttribute('href')||use.getAttribute('xlink:href')).join(' ');
          return /playbutton|play-button|play_button|#play\b/i.test(info)&&!/pause/i.test(info);
        });
        if(button){row.setAttribute('data-liquid-control','search-card');button.setAttribute('data-liquid-control','search');if(action==='search_click')button.click();return true;}
        row.dispatchEvent(new MouseEvent('mouseover',{bubbles:true,view:window}));
      }
    }
    return false;
  }
  return {};
}
