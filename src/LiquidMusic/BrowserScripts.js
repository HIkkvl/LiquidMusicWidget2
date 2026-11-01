({action, service, command, query, volume}) => {
  const norm = text => String(text || '').toLowerCase().replace(/ё/g,'е').replace(/[^\p{L}\p{N}]+/gu,' ').trim();
  const text = selectors => {
    for (const selector of selectors) {
      const el=document.querySelector(selector);
      if(el?.textContent?.trim())return el.textContent.trim();
    }
    return '';
  };
  const playerSelector='[class*="PlayerBar"],ytmusic-player-bar,#movie_player';
  const players=[...document.querySelectorAll(playerSelector)];
  // Яндекс Музыка создаёт `new Audio()` и не кладёт его в DOM: такой элемент не найти через querySelectorAll.
  // Ловим его при play() и принимаем список, который приложение собрало через CDP (Runtime.queryObjects).
  if(service==='yandex'&&!window.__lmHook){
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
  const media=medias.find(el=>!el.paused)||[...medias].reverse().find(usable)||medias[medias.length-1];
  const metadata=navigator.mediaSession?.metadata;
  const label=el=>[el.getAttribute('aria-label'),el.getAttribute('title')].filter(Boolean).join(' ');
  const enabled=el=>!el.hasAttribute('disabled') && el.getAttribute('aria-disabled')!=='true' && el.getAttribute('data-disabled')!=='true';
  const buttons=players.flatMap(root=>[...root.querySelectorAll('button,[role="button"],.ytp-next-button,.ytp-prev-button')]);
  const hasPause=buttons.some(el=>/пауза|pause|кідірту/i.test(label(el)));
  const playing=media?!media.paused:navigator.mediaSession?.playbackState==='playing'||hasPause;
  const patterns={
    next:/следующ|next|келесі/i, previous:/предыдущ|previous|алдыңғы/i,
    pause:/пауза|pause|кідірту/i,play:/воспроизве|слушать|продолж|играть|\bplay\b|resume|ойнату/i,
    like:/мне нравится|понравивш|нравится|\blike\b|liked|unlike|ұнайды/i
  };
  const volumeInput=document.querySelector('input[type="range"][aria-label*="громк" i],input[type="range"][aria-label*="volume" i]');
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
    const image=players.flatMap(root=>[...root.querySelectorAll('img')])[0];
    const trackLink=players.flatMap(root=>[...root.querySelectorAll('a[href*="/track/"],a[href*="/tracks/"],a[href*="watch?v="]')])[0]
      ||(/\/track\/\d+/.test(location.pathname)?{href:location.href}:null)
      ||players.flatMap(root=>[...root.querySelectorAll('a[href*="/album/"]')])[0];
    const streamUrl=media?.currentSrc||media?.src||'';
    let position=Number.isFinite(media?.currentTime)?media.currentTime:0,duration=Number.isFinite(media?.duration)?media.duration:0,timeSource=duration>0?'media':'none';
    if(!(duration>0)){const fallback=timeFromPlayer();if(fallback){position=fallback.position;duration=fallback.duration;timeSource=fallback.source;}}
    const like=buttons.find(el=>patterns.like.test(label(el))&&!/не нравится|dislike|ұнамайды/i.test(label(el)))||document.querySelector('#segmented-like-button button,ytd-segmented-like-dislike-button-renderer like-button-view-model button');
    return {
      connected:true,service,
      title:metadata?.title||text(['[class*="PlayerBar"] [class*="Meta_title"]','[class*="PlayerBar"] [class*="trackTitle"]','[class*="PlayerBar"] [class*="title"]','ytmusic-player-bar .title','ytd-watch-metadata h1'])||'В ожидании музыки',
      artist:metadata?.artist||text(['[class*="PlayerBar"] [class*="Meta_artists"]','[class*="PlayerBar"] [class*="artists"]','ytmusic-player-bar .byline','ytd-video-owner-renderer #channel-name'])||'',
      album:metadata?.album||'',
      artwork:metadata?.artwork?.at(-1)?.src||image?.currentSrc||image?.src||'',
      track_url:trackLink?new URL(trackLink.href,location.href).href:'',page_url:location.href,
      stream_url:/^https?:\/\//i.test(streamUrl)?streamUrl:'',user_agent:navigator.userAgent,
      playing,liked:like?.getAttribute('aria-pressed')==='true'||/убрать|unlike|remove from/i.test(label(like||document.createElement('i'))),
      media_found:!!media,media_stale:!!(media&&media.paused&&hasPause),
      position,duration,time_source:timeSource,stream_kind:/^https?:/i.test(streamUrl)?'direct':streamUrl.startsWith('blob:')?'blob':'none',
      volume:media?.volume??(volumeInput?(Number(volumeInput.value)-Number(volumeInput.min||0))/(Number(volumeInput.max||100)-Number(volumeInput.min||0)):null),error:''
    };
  }
  document.querySelectorAll('[data-liquid-control]').forEach(el=>el.removeAttribute('data-liquid-control'));
  if(action==='volume') {
    if(volumeInput){const setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;setter.call(volumeInput,String(Number(volumeInput.min||0)+volume*(Number(volumeInput.max||100)-Number(volumeInput.min||0))));volumeInput.dispatchEvent(new Event('input',{bubbles:true}));volumeInput.dispatchEvent(new Event('change',{bubbles:true}));return {ok:true};}
    if(media){media.volume=volume;return {ok:true};}
    return {error:'На сайте пока не найден регулятор громкости'};
  }
  if(action==='target') {
    if((command==='pause'||command==='stop')&&!playing)return {already:true};
    if(command==='play'&&playing)return {already:true};
    let target=command==='stop'?'pause':command==='play_pause'?(playing?'pause':'play'):command;
    let button;
    if(service==='youtube') {
      const selector=target==='next'?'.ytp-next-button':target==='previous'?'.ytp-prev-button':target==='like'?'#segmented-like-button button,ytd-segmented-like-dislike-button-renderer like-button-view-model button,ytmusic-like-button-renderer button':'.ytp-play-button,ytmusic-player-bar #play-pause-button';
      button=document.querySelector(selector);
    }
    button ||= buttons.find(el=>enabled(el)&&patterns[target]?.test(label(el))&&!(target==='like'&&/не нравится|dislike/i.test(label(el))));
    if(!button||!enabled(button))return {found:false};
    button.setAttribute('data-liquid-control','target');return {found:true};
  }
  if(action==='search_target'||action==='search_click') {
    const links=[...document.querySelectorAll('a[href*="/track/"],a[href*="/tracks/"]')].filter(el=>!el.closest(playerSelector));
    const words=norm(query).split(' ');
    const seen=new Set();
    for(const link of links) {
      let row=link.parentElement;
      for(let depth=0;row&&row!==document.body&&depth<8;depth++,row=row.parentElement) {
        const paths=new Set([...row.querySelectorAll('a[href*="/track/"],a[href*="/tracks/"]')].map(el=>new URL(el.href,location.origin).pathname));
        if(paths.size>1)break;
        if(seen.has(row))continue;seen.add(row);
        const full=' '+norm(link.textContent+' '+row.innerText+' '+row.getAttribute('aria-label'))+' ';
        if(!words.every(word=>full.includes(' '+word+' ')))continue;
        const button=[...row.querySelectorAll('button,[role="button"]')].find(el=>{
          if(!enabled(el)||/пауза|pause|нравится|like|меню|menu/i.test(label(el)))return false;
          if(patterns.play.test(label(el)))return true;
          const info=el.className+' '+el.getAttribute('data-testid')+' '+[...el.querySelectorAll('use')].map(use=>use.getAttribute('href')||use.getAttribute('xlink:href')).join(' ');
          return /playbutton|play-button|play_button|#play\b/i.test(info)&&!/pause/i.test(info);
        });
        if(button){button.setAttribute('data-liquid-control','search');if(action==='search_click')button.click();return true;}
        row.dispatchEvent(new MouseEvent('mouseover',{bubbles:true,view:window}));
      }
    }
    return false;
  }
  return {};
}
