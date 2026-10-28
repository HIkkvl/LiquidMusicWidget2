({action, service, command, query, volume}) => {
  const cyrillic='абвгдеёжзийклмнопрстуфхцчшщъыьэюя';
  const latin=['a','b','v','g','d','e','e','zh','z','i','y','k','l','m','n','o','p','r','s','t','u','f','h','ts','ch','sh','shch','','y','','e','yu','ya'];
  const norm = text => String(text || '').toLowerCase().replace(/[а-яё]/g,c=>latin[cyrillic.indexOf(c)]).replace(/[^\p{L}\p{N}]+/gu,' ').trim();
  const text = selectors => {
    for (const selector of selectors) {
      const el=document.querySelector(selector);
      if(el?.textContent?.trim())return el.textContent.trim();
    }
    return '';
  };
  const playerSelector='[class*="PlayerBar"],ytmusic-player-bar,#movie_player';
  const players=[...document.querySelectorAll(playerSelector)];
  const mediaElements=[...document.querySelectorAll('audio,video')];
  const media=service==='yandex'?mediaElements.find(el=>el.tagName==='AUDIO'&&!el.paused)||mediaElements.find(el=>el.tagName==='AUDIO'&&el.closest(playerSelector)):mediaElements.find(el=>!el.paused)||mediaElements[0];
  const metadata=navigator.mediaSession?.metadata;
  const label=el=>[el.getAttribute('aria-label'),el.getAttribute('title')].filter(Boolean).join(' ');
  const enabled=el=>!el.hasAttribute('disabled') && el.getAttribute('aria-disabled')!=='true' && el.getAttribute('data-disabled')!=='true';
  const buttons=players.flatMap(root=>[...root.querySelectorAll('button,[role="button"],.ytp-next-button,.ytp-prev-button')]);
  const playPattern=/^(?:воспроизведение|воспроизвести|слушать|продолжить|играть|play|resume|ойнату)(?:\s|$)/i;
  const hasPause=buttons.some(el=>enabled(el)&&/^(?:пауза|pause|кідірту)(?:\s|$)/i.test(label(el)));
  const hasPlay=buttons.some(el=>enabled(el)&&playPattern.test(label(el)));
  // Yandex can play the main track through its internal engine while a paused,
  // muted preview <video> remains in the document. Its state is not the player.
  const playing=service==='yandex'?(hasPause?true:hasPlay?false:navigator.mediaSession?.playbackState==='playing'||!!media&&!media.paused):media?!media.paused:navigator.mediaSession?.playbackState==='playing'||hasPause;
  const patterns={
    next:/следующ|next|келесі/i, previous:/предыдущ|previous|алдыңғы/i,
    pause:/пауза|pause|кідірту/i,play:playPattern,
    like:/мне нравится|понравивш|нравится|\blike\b|liked|unlike|ұнайды/i
  };
  const volumeInput=document.querySelector('input[type="range"][aria-label*="громк" i],input[type="range"][aria-label*="volume" i]');
  if(action==='snapshot') {
    const image=players.flatMap(root=>[...root.querySelectorAll('img')])[0];
    const like=buttons.find(el=>patterns.like.test(label(el))&&!/не нравится|dislike|ұнамайды/i.test(label(el)))||document.querySelector('#segmented-like-button button,ytd-segmented-like-dislike-button-renderer like-button-view-model button');
    return {
      connected:true,service,
      title:metadata?.title||text(['[class*="PlayerBar"] [class*="Meta_title"]','[class*="PlayerBar"] [class*="trackTitle"]','[class*="PlayerBar"] [class*="title"]','ytmusic-player-bar .title','ytd-watch-metadata h1'])||'В ожидании музыки',
      artist:metadata?.artist||text(['[class*="PlayerBar"] [class*="Meta_artists"]','[class*="PlayerBar"] [class*="artists"]','ytmusic-player-bar .byline','ytd-video-owner-renderer #channel-name'])||'',
      artwork:metadata?.artwork?.at(-1)?.src||image?.currentSrc||image?.src||'',
      playing,liked:like?.getAttribute('aria-pressed')==='true'||/убрать|unlike|remove from/i.test(label(like||document.createElement('i'))),
      position:Number.isFinite(media?.currentTime)?media.currentTime:0,duration:Number.isFinite(media?.duration)?media.duration:0,
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
        const artistNames=[...row.querySelectorAll('a[href*="/artist/"]')].map(el=>el.textContent).join(' ');
        const full=' '+norm(link.textContent+' '+artistNames+' '+row.innerText+' '+row.getAttribute('aria-label'))+' ';
        if(!words.every(word=>full.includes(' '+word+' ')))continue;
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
