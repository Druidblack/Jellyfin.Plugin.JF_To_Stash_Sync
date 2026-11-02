/* JF To Stash Sync — media slideshow, native Jellyfin API, matching the actor hero design.
 * This is a new, lightweight implementation; it does not load Media Bar Enhanced.
 */
(function () {
  'use strict';
  const T = (text) => window.__jfToStashI18n?.t(text) ?? String(text ?? '');
  if (window.__jfFeaturedMediaBannerLoaded) return;
  window.__jfFeaturedMediaBannerLoaded = true;
  const PREFIX = 'jf-mediahero-';
  const ID = PREFIX + 'section';
  const CACHE_MS = 5 * 60 * 1000;
  const RANDOM_HISTORY_PREFIX = 'jf-to-stash-featured-media-recent-v1:';
  const RANDOM_HISTORY_MS = 7 * 24 * 60 * 60 * 1000;
  const RANDOM_HISTORY_LIMIT = 300;
  const safeGuid = /^[a-f\d-]{32,36}$/i;
  const state = {
    enabled: false, user: '', settings: null, items: [], current: 0, exp: 0,
    running: null, generation: 0, timer: 0, tickAt: 0, remainingMs: 0, endAt: 0,
    hovered: false, error: '', cardId: '', startX: 0
  };
  let mountPending = false;
  // Keep the completed card and its event handlers while Jellyfin replaces the Home view.
  let retainedSection = null;
  const api = () => window.ApiClient;
  const get = (o, camel, pascal, fallback) => o && o[camel] !== undefined ? o[camel] :
    (o && o[pascal] !== undefined ? o[pascal] : fallback);
  function userId() { try { return api()?.getCurrentUserId?.() || ''; } catch (_) { return ''; } }
  function isHome() {
    const frag = String(location.hash || '').replace(/^#!?/, '');
    const route = frag || String(location.pathname || '');
    const i = route.indexOf('?');
    const path = i < 0 ? route : route.substring(0, i);
    if (!/(?:^|\/)home\/?$/i.test(path)) return false;
    const qs = i < 0 ? (!frag ? location.search.slice(1) : '') : route.slice(i + 1);
    const tab = new URLSearchParams(qs).get('tab');
    return tab === null || tab === '' || tab === '0';
  }
  function visible(e) { return !!(e?.isConnected && e.getClientRects().length &&
    getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none'); }
  function homeRoot() {
    if (!isHome()) return null;
    for (const sel of ['.homeSectionsContainer','.homePage .homeSections',
      '.homePage .sectionsContainer','.homeView .homeSectionsContainer']) {
      const e = [...document.querySelectorAll(sel)].find(visible);
      if (e) return e;
    }
    const home = [...document.querySelectorAll('.homePage,.homeView')]
      .find(e => visible(e) && e.querySelector('.verticalSection'));
    return home?.querySelector('.verticalSection')?.parentElement || null;
  }
  async function json(path) {
    const c = api();
    if (!c?.fetch || !c?.getUrl) throw new Error('Jellyfin API unavailable');
    const result = await c.fetch({ url: c.getUrl(path), type: 'GET' });
    if (!result.ok) throw new Error('Jellyfin HTTP ' + result.status);
    return result.json();
  }
  function id(v) { return String(v || '').replace(/-/g, '').toLowerCase(); }
  function itemId(item) { return get(item,'id','Id',''); }
  function images(item) { return get(item,'imageTags','ImageTags',{}) || {}; }
  function tag(item,type) { const tags = images(item); return tags[type] || tags[type.toLowerCase()] || ''; }
  function backdrop(item) {
    const tags = get(item,'backdropImageTags','BackdropImageTags',[]);
    const tag0 = Array.isArray(tags) ? tags[0] : '';
    const fallback = tag(item,'Backdrop');
    if (!tag0 && !fallback) return '';
    const objId = itemId(item);
    if (!objId) return '';
    return api().getUrl('Items/' + encodeURIComponent(objId) + '/Images/Backdrop/0',
      { tag: tag0 || fallback, maxWidth: 1920, quality: 88 });
  }
  function logo(item) {
    const image = tag(item,'Logo');
    if (!image) return '';
    return api().getUrl('Items/' + encodeURIComponent(itemId(item)) + '/Images/Logo/0',
      { tag: image, maxWidth: 900, quality: 90 });
  }
  function detailLink(item) {
    return (location.hash.startsWith('#!') ? '#!/details?id=' : '#/details?id=') + encodeURIComponent(itemId(item));
  }
  function imageReady(item,s) {
    const type = get(item,'type','Type','');
    if (type !== 'Movie' && type !== 'Series') return false;
    if (!backdrop(item)) return false;
    if (s.requireLogo && !logo(item)) return false;
    if (!s.includeWatched && get(get(item,'userData','UserData',{}),'played','Played',false)) return false;
    return true;
  }
  function shuffle(input) {
    const a = input.slice();
    for (let i=a.length-1; i>0; i--) {
      const k=Math.floor(Math.random()*(i+1));
      [a[i],a[k]]=[a[k],a[i]];
    }
    return a;
  }
  // Avoid repeatedly selecting the same titles when Jellyfin Web is reloaded.
  // This is a soft preference, not a hard filter: small libraries still work.
  function readRandomHistory(user) {
    if(!user)return [];
    try {
      const stored=JSON.parse(localStorage.getItem(RANDOM_HISTORY_PREFIX+id(user))||'null');
      if(!stored || !Array.isArray(stored.ids) ||
         !Number.isFinite(stored.savedAt) || Date.now()-stored.savedAt>RANDOM_HISTORY_MS ||
         stored.savedAt>Date.now()+60000)return [];
      return [...new Set(stored.ids.filter(x=>typeof x==='string'&&safeGuid.test(x)).map(id))]
        .slice(0,RANDOM_HISTORY_LIMIT);
    }catch(_){return [];}
  }
  function rememberRandomSlides(user,items,history) {
    if(!user||!items.length)return;
    const current=items.map(x=>id(itemId(x))).filter(Boolean);
    try {
      localStorage.setItem(RANDOM_HISTORY_PREFIX+id(user),JSON.stringify({
        savedAt:Date.now(),ids:[...new Set([...current,...history])].slice(0,RANDOM_HISTORY_LIMIT)
      }));
    }catch(_){/* Storage can be disabled, full or blocked in private mode. */}
  }
  function parseIds(csv,limit) {
    return [...new Set(String(csv||'').split(/[\s,;]+/).map(x=>x.trim()).filter(x=>safeGuid.test(x)))]
      .slice(0,limit);
  }
  function defaults(conf) {
    const seconds = Number(get(conf,'featuredMediaSlideSeconds','FeaturedMediaSlideSeconds',15));
    const maximum = Number(get(conf,'featuredMediaMaxItems','FeaturedMediaMaxItems',30));
    const order = get(conf,'featuredMediaSortBy','FeaturedMediaSortBy','Random');
    return {
      includeWatched: !!get(conf,'featuredMediaIncludeWatched','FeaturedMediaIncludeWatched',false),
      requireLogo: !!get(conf,'featuredMediaRequireLogo','FeaturedMediaRequireLogo',false),
      overview: get(conf,'featuredMediaShowOverview','FeaturedMediaShowOverview',true) !== false,
      ratings: get(conf,'featuredMediaShowRatings','FeaturedMediaShowRatings',true) !== false,
      progress: get(conf,'featuredMediaShowProgress','FeaturedMediaShowProgress',true) !== false,
      pauseOnHover: !!get(conf,'featuredMediaPauseOnHover','FeaturedMediaPauseOnHover',false),
      sort: ['Random','DateCreated','PremiereDate','CommunityRating','SortName'].includes(order)?order:'Random',
      seconds: Math.max(5, Math.min(120, Number.isFinite(seconds)?seconds:15)),
      max: Math.max(1, Math.min(100, Number.isFinite(maximum)?maximum:30)),
      libraries: parseIds(get(conf,'featuredMediaLibraryIds','FeaturedMediaLibraryIds',''),16)
    };
  }
  function communityScore(item) {
    const raw=get(item,'communityRating','CommunityRating',null);
    if(raw===null || raw===undefined || raw==='') return -1;
    const value=Number(raw);
    return Number.isFinite(value) && value>=0 ? value : -1;
  }
  function ratingBand(item) {
    const rating=communityScore(item);
    return rating<0 ? -1 : Math.max(0,Math.min(10,Math.floor(rating)));
  }
  function byCommunityRating(a,b) {
    return communityScore(b)-communityScore(a) ||
      String(get(a,'name','Name','')).localeCompare(String(get(b,'name','Name','')));
  }
  // Reserve one item per available rating band, then fill the remaining slides
  // with the highest-rated eligible items. The slideshow itself stays sorted
  // from highest to lowest. This avoids a top-100 of 9/10 ratings crowding out
  // every movie/series rated 8, 7, 6, 5 and below.
  function selectRatedSlides(top,representatives,maximum) {
    const unique=new Map();
    const put=item=>{ const key=id(itemId(item));if(key&&!unique.has(key))unique.set(key,item); };
    const available=[...representatives.entries()].sort((a,b)=>b[0]-a[0]).map(pair=>pair[1]);
    if(available.length<=maximum) available.forEach(put);
    else if(maximum===1) put(available[0]);
    else {
      // Evenly cover the available score range if the requested slide count
      // is smaller than the number of distinct integer rating bands.
      for(let i=0;i<maximum;i++)
        put(available[Math.round(i*(available.length-1)/(maximum-1))]);
    }
    for(const item of top) {
      if(unique.size>=maximum) break;
      put(item);
    }
    return [...unique.values()].sort(byCommunityRating).slice(0,maximum);
  }
  async function loadItems(settings, generation, firstSlide) {
    // Both movies and series are always eligible (there is no content-type toggle).
    const types = 'Movie,Series';
    const sortServer=settings.sort==='Random'?'DateCreated':settings.sort;
    const ascending=settings.sort==='SortName';
    const limit=Math.min(400,Math.max(100,settings.max*4));
    const parents=settings.libraries.length?settings.libraries:[''];
    const query=async params=>{
      const ret=await json('Items?'+params.toString());
      return get(ret,'items','Items',[]);
    };
    function paramsFor(parentId,sort,order,pageSize,startIndex=0,minRating) {
      const params=new URLSearchParams({UserId:state.user,IncludeItemTypes:types,Recursive:'true',
        EnableImages:'true',EnableUserData:'true',Fields:'Overview,Genres,DateCreated,PremiereDate,CommunityRating',
        SortBy:sort,SortOrder:order,EnableTotalRecordCount:'false',Limit:String(pageSize)});
      if(parentId)params.set('ParentId',parentId);
      if(startIndex)params.set('StartIndex',String(startIndex));
      if(minRating!==undefined)params.set('MinCommunityRating',String(minRating));
      return params;
    }
    if(settings.sort==='Random') {
      // Shuffling the newest N titles only repeats the same titles every time.
      // Ask Jellyfin for the actual library size, then sample bounded, random
      // pages across the *whole* filtered Movie/Series catalogue.
      const pageSize=Math.min(120,Math.max(48,settings.max*2));
      const maxPages=Math.min(32,Math.max(14,Math.ceil(settings.max/4)+10));
      const minPages=Math.min(maxPages,Math.max(8,Math.ceil(settings.max/16)+5));
      const targetFresh=Math.max(settings.max*2,settings.max+16);
      const history=readRandomHistory(state.user);
      const recent=new Set(history);
      const historyAge=new Map(history.map((key,index)=>[key,index]));
      const available=new Map();
      const fallbackItems=[];
      let previewId='';
      const stillCurrent=()=>generation===state.generation;
      const states=[];
      let successfulQueries=0;
      function addCandidates(items){
        if(!Array.isArray(items))return;
        for(const item of items){
          const key=id(itemId(item));
          if(key&&!available.has(key)&&imageReady(item,settings)){
            available.set(key,item);
            if(!previewId&&!recent.has(key)&&firstSlide&&stillCurrent()){
              previewId=key;
              firstSlide(item);
              rememberRandomSlides(state.user,[item],history);
            }
          }
        }
      }
      // One inexpensive request per selected library, with a real total count.
      // Sorting by title keeps page offsets stable across repeated requests.
      async function discover(parentId){
        const params=paramsFor(parentId,'SortName','Ascending',1);
        params.set('EnableTotalRecordCount','true');
        try {
          const result=await json('Items?'+params.toString());
          if(!stillCurrent())return;
          successfulQueries++;
          const items=get(result,'items','Items',[]);
          if(Array.isArray(items))fallbackItems.push(...items);
          const reported=Number(get(result,'totalRecordCount','TotalRecordCount',NaN));
          if(Number.isSafeInteger(reported)&&reported>=0 && (reported!==0 || !items?.length)){
            if(reported>0)states.push({parentId,count:reported,pages:Math.ceil(reported/pageSize),
              used:new Set(),unknown:false,offset:0});
          }else {
            // Older/incompatible API responses without TotalRecordCount:
            // continue with bounded sequential pagination rather than failing.
            if(Array.isArray(items)&&items.length)
              states.push({parentId,count:0,pages:0,used:new Set(),unknown:true,offset:0});
          }
        }catch(error){
          console.warn('[JFToStashSync] Featured media: random library count failed:',error);
        }
      }
      for(let i=0;i<parents.length;i+=3){
        await Promise.all(parents.slice(i,i+3).map(discover));
        if(!stillCurrent())return [];
      }
      if(!successfulQueries)throw new Error('Jellyfin random library queries failed');
      // Shuffle the first sampling pass so every chosen library gets a chance.
      const firstPass=shuffle(states);
      function hasPages(entry){return entry.unknown || entry.used.size<entry.pages;}
      function pickLibrary(){
        while(firstPass.length){
          const entry=firstPass.pop();
          if(hasPages(entry))return entry;
        }
        const remaining=states.filter(hasPages);
        if(!remaining.length)return null;
        // Weight by remaining catalogue size; small libraries do not dominate.
        const weights=remaining.map(e=>e.unknown?pageSize:Math.max(1,e.pages-e.used.size)*pageSize);
        let pick=Math.random()*weights.reduce((a,b)=>a+b,0);
        for(let i=0;i<remaining.length;i++){
          pick-=weights[i];
          if(pick<0)return remaining[i];
        }
        return remaining[remaining.length-1];
      }
      function nextOffset(entry){
        if(entry.unknown){const offset=entry.offset;entry.offset+=pageSize;return offset;}
        if(entry.used.size>=entry.pages)return null;
        let slot=Math.floor(Math.random()*entry.pages);
        for(let attempts=0;entry.used.has(slot)&&attempts<entry.pages;attempts++)
          slot=(slot+1)%entry.pages;
        entry.used.add(slot);
        return slot*pageSize;
      }
      let requested=0;
      while(stillCurrent() && requested<maxPages){
        const batch=[];
        for(let i=0;i<3&&requested<maxPages;i++){
          const entry=pickLibrary();
          if(!entry)break;
          const offset=nextOffset(entry);
          if(offset===null)break;
          requested++;
          batch.push((async()=>{
            try {
              const found=await query(paramsFor(entry.parentId,'SortName','Ascending',pageSize,offset));
              if(!stillCurrent())return;
              successfulQueries++;
              addCandidates(found);
              if(entry.unknown && (!Array.isArray(found)||found.length<pageSize))
                entry.unknown=false; // no more uncounted pages
            }catch(error){
              if(entry.unknown)entry.unknown=false;
              console.warn('[JFToStashSync] Featured media: random sample failed:',error);
            }
          })());
        }
        if(!batch.length)break;
        await Promise.all(batch);
        const fresh=[...available.keys()].filter(key=>!recent.has(key)).length;
        if(requested>=minPages && fresh>=targetFresh)break;
      }
      if(!stillCurrent())return [];
      // If sampling requests all failed, keep any valid count-page results
      // instead of turning an already populated library into an empty banner.
      if(!available.size){
        for(const entry of states.slice(0,3)){
          try {
            const found=await query(paramsFor(entry.parentId,'SortName','Ascending',pageSize));
            if(!stillCurrent())return [];
            addCandidates(found);
            if(available.size)break;
          }catch(_){/* Keep other libraries available on temporary failures. */}
        }
      }
      if(!available.size)addCandidates(fallbackItems);
      // Prioritize films absent from recent selections. When there aren't
      // enough new films, prefer the *least* recently selected old titles.
      const shuffled=shuffle([...available.values()]);
      const fresh=shuffled.filter(item=>!recent.has(id(itemId(item))));
      const old=shuffled.filter(item=>recent.has(id(itemId(item))))
        .sort((a,b)=>(historyAge.get(id(itemId(b)))??0)-(historyAge.get(id(itemId(a)))??0));
      let chosen=[...fresh,...old].slice(0,settings.max);
      // Keep the randomly selected first preview in the final slideshow too.
      // Prevents the first image from changing after sampling completes.
      if(previewId && available.has(previewId)){
        chosen=[available.get(previewId),...chosen.filter(item=>id(itemId(item))!==previewId)]
          .slice(0,settings.max);
      }
      if(!previewId&&firstSlide&&chosen.length)firstSlide(chosen[0]);
      rememberRandomSlides(state.user,chosen,history);
      return chosen;
    }
    if(settings.sort==='CommunityRating') {
      const top=[];
      const seenTop=new Set();
      const representatives=new Map();
      const pageSize=Math.min(120,Math.max(60,settings.max*2));
      // A bounded number of secondary probes prevents an absent rating band,
      // or a library with many unusable images, from blocking the slideshow.
      const maxFollowupPages=24;
      const maxBandQueries=44;
      let bandQueries=0;
      let bandQueriesUnsupported=false;
      let successfulQueries=0;
      let lastPreview='';
      const nextOffsets=new Map();
      const exhausted=new Set();
      const stillCurrent=()=>generation===state.generation;
      function preview(){
        if(!firstSlide||!stillCurrent())return;
        const slides=selectRatedSlides(top,representatives,settings.max);
        if(!slides.length)return;
        const signature=slides.map(x=>id(itemId(x))).join(',');
        if(signature===lastPreview)return;
        lastPreview=signature;
        firstSlide(slides);
      }
      function addTop(found){
        for(const item of found){
          const key=id(itemId(item));
          if(!key||seenTop.has(key)||communityScore(item)<0||!imageReady(item,settings))continue;
          seenTop.add(key);top.push(item);
        }
        top.sort(byCommunityRating);
        preview();
      }
      // Each selected library gets an initial high-to-low page before the
      // supplementary range requests. Results become visible immediately.
      async function loadInitial(parentId){
        try {
          const found=await query(paramsFor(parentId,'CommunityRating','Descending',pageSize));
          if(!stillCurrent())return;
          successfulQueries++;
          if(!Array.isArray(found)||!found.length){exhausted.add(parentId);return;}
          addTop(found);
          nextOffsets.set(parentId,found.length);
          if(found.length<pageSize)exhausted.add(parentId);
        }catch(error){
          exhausted.add(parentId);
          console.warn('[JFToStashSync] Featured media: initial rating page failed:',error);
        }
      }
      // Only a handful of requests in flight at once; do not overload Jellyfin.
      async function runLimited(inputs,concurrency,work){
        let cursor=0;
        await Promise.all(Array.from({length:Math.min(concurrency,inputs.length)},async()=>{
          while(cursor<inputs.length&&stillCurrent()){
            const index=cursor++;
            await work(inputs[index]);
          }
        }));
      }
      await runLimited(parents,3,loadInitial);
      if(!stillCurrent())return [];

      // Probe score bands directly: a descending first page may contain only
      // 9/10-rated items even though 1..8-rated items are present later.
      // Most bands need one query; at most two extra pages are examined when
      // a candidate lacks a Backdrop / Logo or is already watched.
      async function probeBand(band){
        for(const parentId of parents){
          let offset=0;
          for(let page=0;page<3;page++){
            if(!stillCurrent()||bandQueriesUnsupported||bandQueries>=maxBandQueries)return;
            bandQueries++;
            let found;
            try {
              found=await query(paramsFor(parentId,'CommunityRating',
                band===10?'Descending':'Ascending',pageSize,offset,band===0?0.01:band));
              if(!stillCurrent())return;
              successfulQueries++;
            }catch(error){
              // A server that rejects MinCommunityRating must not leave the
              // banner stuck on its one-item initial preview.
              if(/Jellyfin HTTP 400/.test(String(error)))bandQueriesUnsupported=true;
              console.warn('[JFToStashSync] Featured media: rating band '+band+' unavailable:',error);
              break;
            }
            if(!Array.isArray(found)||!found.length)break;
            const candidate=found.find(item=>ratingBand(item)===band&&imageReady(item,settings));
            if(candidate){
              if(!representatives.has(band)){
                representatives.set(band,candidate);
                preview();
              }
              return;
            }
            if(found.length<pageSize||(band!==10&&found.some(item=>ratingBand(item)>band)))break;
            offset+=found.length;
          }
        }
      }
      // Supplementary bands and ordinary descending pagination run together.
      // A slow/unsupported band query cannot postpone already available slides.
      async function fillFromTop(){
        let remainingPages=maxFollowupPages;
        for(const parentId of parents){
          let offset=nextOffsets.get(parentId)||0;
          while(stillCurrent()&&!exhausted.has(parentId)&&remainingPages>0&&top.length<settings.max){
            remainingPages--;
            let found;
            try {
              found=await query(paramsFor(parentId,'CommunityRating','Descending',pageSize,offset));
              if(!stillCurrent())return;
              successfulQueries++;
            }catch(error){
              console.warn('[JFToStashSync] Featured media: next rating page failed:',error);
              break;
            }
            if(!Array.isArray(found)||!found.length)break;
            addTop(found);
            if(found.length<pageSize)break;
            offset+=found.length;
          }
        }
      }
      await Promise.all([
        runLimited([9,8,7,6,5,4,3,2,1,0,10],3,probeBand),
        fillFromTop()
      ]);
      if(!stillCurrent())return [];
      if(!successfulQueries)throw new Error('Jellyfin rating queries failed');
      return selectRatedSlides(top,representatives,settings.max);
    }
    let all=[];
    for(const parentId of parents) {
      if(generation!==state.generation)return [];
      const found=await query(paramsFor(parentId,sortServer,ascending?'Ascending':'Descending',limit));
      if(generation!==state.generation)return [];
      if(Array.isArray(found)) {
        all.push(...found);
        if(firstSlide) {
          const eligible=found.filter(x=>imageReady(x,settings));
          if(settings.sort==='SortName')
            eligible.sort((a,b)=>String(get(a,'sortName','SortName',get(a,'name','Name','')))
              .localeCompare(String(get(b,'sortName','SortName',get(b,'name','Name','')))));
          else if(settings.sort==='DateCreated'||settings.sort==='PremiereDate') {
            const key=settings.sort[0].toLowerCase()+settings.sort.slice(1);
            eligible.sort((a,b)=>String(get(b,key,settings.sort,''))
              .localeCompare(String(get(a,key,settings.sort,''))));
          }
          const first=eligible[0];
          if(first){firstSlide(first);firstSlide=null;}
        }
      }
    }
    const seen=new Set();
    all=all.filter(x=>{
      const guid=id(itemId(x));
      if(!guid||seen.has(guid))return false;
      seen.add(guid);return imageReady(x,settings);
    });
    return all.slice(0,settings.max);
  }
  function stylesOnce() {
    if(document.getElementById(PREFIX+'css')) return;
    const style=document.createElement('style');
    style.id=PREFIX+'css';
    style.textContent=`
      .${PREFIX}section{box-sizing:border-box;padding:0 3.3%;margin:1.4em 0 1.8em;min-width:0}
      .${PREFIX}card{position:relative;isolation:isolate;width:100%;aspect-ratio:16/9;min-height:0;overflow:hidden;border-radius:16px;background:#141414;color:#fff;box-shadow:0 10px 32px #0005}
      .${PREFIX}backdrop{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;object-position:center;z-index:-2;
        transform-origin:center center;animation:${PREFIX}kenBurns var(--${PREFIX}zoom-duration,15000ms) cubic-bezier(.25,.1,.25,1) both;
        backface-visibility:hidden;will-change:transform}
      @keyframes ${PREFIX}kenBurns{from{transform:scale(1)}to{transform:scale(1.10)}}
      .${PREFIX}card.${PREFIX}zoomPaused .${PREFIX}backdrop{animation-play-state:paused}
      .${PREFIX}shade{position:absolute;inset:0;background:linear-gradient(90deg,#090909df 0%,#07070791 40%,#0003 86%),linear-gradient(0deg,#050505b8 0%,transparent 70%);z-index:-1}
      .${PREFIX}content{display:flex;flex-direction:column;justify-content:space-between;gap:1em;position:absolute;inset:0;min-height:0;box-sizing:border-box;padding:1.1rem 1.35rem 1rem}
      .${PREFIX}top{display:flex;justify-content:flex-end;align-items:flex-start;gap:.45rem}
      .${PREFIX}nav{display:flex;gap:.4rem;align-items:center}
      .${PREFIX}btn{appearance:none;background:#101010a8;backdrop-filter:blur(7px);border:1px solid #ffffff27;border-radius:30px;color:#fff;cursor:pointer;font:inherit;font-weight:600;padding:.55rem .88rem;text-decoration:none!important}
      .${PREFIX}btn:hover{background:#333c}
      .${PREFIX}btn:focus-visible{outline:2px solid white;outline-offset:2px}
      /* Reserved grid tracks keep the image, title, metadata, synopsis and controls
         in identical positions even when the next item has shorter or missing fields. */
      .${PREFIX}body{display:grid;grid-template-rows:clamp(64px,11vw,150px) clamp(35px,4.4vw,55px) 34px clamp(45px,5.8vw,82px) 48px;align-content:end;align-items:center;width:100%;max-width:min(780px,100%);gap:.5rem;min-height:0}
      .${PREFIX}logoSlot,.${PREFIX}nameSlot,.${PREFIX}metaSlot,.${PREFIX}overviewSlot{width:100%;height:100%;min-height:0;min-width:0;overflow:hidden;display:flex;align-items:center}
      .${PREFIX}logoSlot{align-items:flex-end}
      .${PREFIX}nameSlot{align-items:center}
      .${PREFIX}overviewSlot{align-items:flex-start}

      .${PREFIX}logo{display:block;width:auto;height:auto;max-width:min(720px,100%);max-height:100%;object-fit:contain;object-position:left bottom;border-radius:8px;filter:drop-shadow(0 4px 12px #000a)}
      .${PREFIX}name{color:white;margin:0;font-size:clamp(1.4rem,3.35vw,2.85rem);font-weight:700;line-height:1.15;text-decoration:none!important;text-shadow:0 3px 12px #0009;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
      .${PREFIX}meta{display:flex;gap:.55rem;align-items:center;flex-wrap:nowrap;white-space:nowrap;overflow:hidden;font-size:.93em;text-shadow:0 2px 6px #000c}
      .${PREFIX}meta span{padding:.27rem .52rem;background:#111a;border-radius:7px}
      .${PREFIX}overview{margin:0;max-width:660px;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;text-overflow:ellipsis;font-size:.95rem;line-height:1.5;text-shadow:0 2px 6px #000c}
      .${PREFIX}actions{display:flex;gap:.7rem;align-items:center;flex-wrap:nowrap;margin-top:0;min-width:0}
      .${PREFIX}favorite{display:inline-flex;align-items:center;justify-content:center;gap:.5em}
      .${PREFIX}favorite svg{flex:none}
      .${PREFIX}favorite.${PREFIX}isfavorite{color:#ef9196;border-color:#ef919680;background:#3d1c20b8}
      .${PREFIX}favorite.${PREFIX}pending{opacity:.55;cursor:wait}
      .${PREFIX}progress{position:absolute;bottom:0;left:0;height:3px;width:100%;background:#fff3}
      .${PREFIX}progressfill{height:100%;background:#ffffffd9;width:0;transition:none;transform-origin:left center}
      .${PREFIX}counter{font-size:.83em;opacity:.9;padding:.5rem .7rem;background:#1119;border-radius:20px}
      .${PREFIX}empty{font-size:.9em;opacity:.8}
      @media(max-width:900px){.${PREFIX}body{grid-template-rows:clamp(50px,10vw,105px) 36px 28px 50px 38px;gap:.35rem}.${PREFIX}overview{font-size:.85rem;line-height:1.4;-webkit-line-clamp:2}.${PREFIX}actions{gap:.3rem}.${PREFIX}actions .${PREFIX}btn{font-size:.8rem;padding:.35rem .55rem}}
      @media(max-width:640px){.${PREFIX}section{padding:0 3%}.${PREFIX}content{padding:.55rem;gap:.15rem}.${PREFIX}body{grid-template-rows:clamp(30px,9vw,48px) 28px 21px 0 28px;gap:.15rem}.${PREFIX}top{min-height:0}.${PREFIX}name{font-size:clamp(.85rem,3.7vw,1.2rem)}.${PREFIX}overviewSlot{display:none}.${PREFIX}meta{font-size:.68rem;gap:.2rem}.${PREFIX}meta span{padding:.12rem .24rem}.${PREFIX}actions{gap:.22rem}.${PREFIX}actions .${PREFIX}btn{padding:.24rem .37rem;font-size:.67rem}.${PREFIX}counter{padding:.25rem .4rem;font-size:.7rem}}
      @media(max-width:400px){.${PREFIX}meta span:nth-child(n+3){display:none}.${PREFIX}actions .${PREFIX}btn{font-size:.63rem}}
      /* Allow the user-enabled banner zoom and countdown to work even when a
         browser advertises reduced motion. Previously the progress transition
         snapped to 100%, only revealing its real position while paused on hover,
         and the Ken Burns animation disappeared on both banner types. */
    `;
    document.head.appendChild(style);
  }
  function elt(tag,cls,text) {
    const node=document.createElement(tag);
    if(cls) node.className=PREFIX+cls;
    if(text!==undefined) node.textContent=T(text);
    return node;
  }
  function anchor() { return document.getElementById('jf-phhero-section') ||
    document.querySelector('#jf-stashdb-trending-section,#jf-stashdb-popularity-section,.jf-spp-section,.jf-bpt-section'); }
  function mount() {
    const root=homeRoot(); if(!root) return null;
    let section=document.getElementById(ID) || retainedSection;
    if(!section){
      section=elt('section','section');section.id=ID;
      retainedSection = section;
      // Listen on the stable outer section: replacing slide DOM must not lose
      // hover state, keyboard navigation or the optional pause-on-hover.
      function syncHover(event) {
        const card = section.querySelector('.' + PREFIX + 'card');
        if (!card) return;
        const rect = card.getBoundingClientRect();
        const inside = event.clientX >= rect.left && event.clientX < rect.right &&
          event.clientY >= rect.top && event.clientY < rect.bottom;
        if (inside === state.hovered) return;
        state.hovered = inside;
        if (state.settings?.pauseOnHover) {
          if (inside) pauseTimer();
          else resumeTimer();
        }
        syncZoomPlayback();
      }
      // The outer section has side padding. Only the actual 16:9 card pauses.
      section.addEventListener('pointerenter', syncHover);
      section.addEventListener('pointermove', syncHover);
      section.addEventListener('pointerleave', () => {
        if (!state.hovered) return;
        state.hovered = false;
        if (state.settings?.pauseOnHover) resumeTimer();
        syncZoomPlayback();
      });
    }
    retainedSection = section;
    const next=anchor();
    if(next && next!==section && next.parentNode===root){
      if(section.parentNode!==root||section.nextElementSibling!==next) root.insertBefore(section,next);
    } else if(section.parentNode!==root) root.insertBefore(section,root.firstChild);
    return section;
  }
  function durationMs(){return (state.settings?.seconds || 15)*1000;}
  function syncZoomPlayback(){
    const card = retainedSection?.querySelector('.'+PREFIX+'card');
    if(card) card.classList.toggle(PREFIX+'zoomPaused',
      document.visibilityState==='hidden'||!isHome()||(state.settings?.pauseOnHover&&state.hovered));
  }
  function stop(){
    if(state.timer)clearTimeout(state.timer);
    state.timer=0;state.tickAt=0;state.endAt=0;state.remainingMs=0;
  }
  function pauseTimer(){
    if(!state.timer)return;
    state.remainingMs=Math.max(1,state.endAt-Date.now());
    clearTimeout(state.timer);state.timer=0;state.tickAt=0;state.endAt=0;
    updateProgress(state.remainingMs,false);
    syncZoomPlayback();
  }
  function updateProgress(remaining,running){
    const bar=document.querySelector('#'+ID+' .'+PREFIX+'progressfill');
    if(!bar)return;
    const total=durationMs();
    const percent=Math.max(0,Math.min(100,100*(1-remaining/total)));
    // Stop the CSS transition at exactly the time remaining, not when a frame
    // next happens to paint. This also cancels any previous animation.
    bar.style.transition='none';bar.style.width=percent+'%';
    if(running&&remaining>0){
      void bar.offsetWidth;
      bar.style.transition='width '+remaining+'ms linear';bar.style.width='100%';
    }
  }
  function resumeTimer(){
    if(state.timer||!state.enabled||state.items.length<2||!isHome()||
       document.visibilityState==='hidden'||(state.hovered&&state.settings?.pauseOnHover))return;
    const ms=state.remainingMs>0?state.remainingMs:durationMs();
    state.remainingMs=ms;
    state.tickAt=Date.now();state.endAt=state.tickAt+ms;
    updateProgress(ms,true);
    syncZoomPlayback();
    state.timer=setTimeout(()=>{
      state.timer=0;state.tickAt=0;state.endAt=0;state.remainingMs=0;
      if(!isHome())return;
      if(state.hovered&&state.settings?.pauseOnHover){
        state.remainingMs=1;updateProgress(1,false);return;
      }
      slide(1);
    },ms);
  }
  function unmount(){
    // Navigation is not a reason to discard the rendered card or its timer position.
    pauseTimer();
    state.hovered=false;
    syncZoomPlayback();
    const section=document.getElementById(ID);
    if(section){retainedSection=section;section.remove();}
  }
  function preloadNeighbors(){
    if(!state.items.length)return;
    const length=state.items.length;
    const positions=[state.current,(state.current+1)%length,(state.current+2)%length,(state.current+length-1)%length];
    window.__jfHomeWarmup?.preloadImages?.(positions.flatMap(i=>[backdrop(state.items[i]),logo(state.items[i])]));
  }
  function slide(direction){
    if(!state.items.length)return;
    state.current=(state.current+direction+state.items.length)%state.items.length;
    draw(true);
    preloadNeighbors();
  }
  function schedule(){
    stop();
    if(!state.enabled||state.items.length<2||!isHome())return;
    state.remainingMs=durationMs();
    updateProgress(state.remainingMs,false);
    resumeTimer();
  }
  function isFavorite(item) {
    return !!get(get(item,'userData','UserData',{}),'isFavorite','IsFavorite',false);
  }
  function favoriteGlyph(active) {
    const ns='http://www.w3.org/2000/svg';
    const svg=document.createElementNS(ns,'svg');
    svg.setAttribute('viewBox','0 0 24 24');
    svg.setAttribute('width','20');
    svg.setAttribute('height','20');
    svg.setAttribute('aria-hidden','true');
    svg.setAttribute('fill',active?'currentColor':'none');
    svg.setAttribute('stroke','currentColor');
    svg.setAttribute('stroke-width','2');
    svg.setAttribute('stroke-linecap','round');
    svg.setAttribute('stroke-linejoin','round');
    const heart=document.createElementNS(ns,'path');
    heart.setAttribute('d','M20.84 4.61a5.5 5.5 0 0 0-7.78 0L12 5.67l-1.06-1.06a5.5 5.5 0 0 0-7.78 7.78L12 21.23l8.84-8.84a5.5 5.5 0 0 0 0-7.78Z');
    svg.appendChild(heart);
    return svg;
  }
  function showFavoriteState(button,item) {
    const active=isFavorite(item);
    const label=T(active?'Убрать из избранного':'Добавить в избранное');
    button.classList.toggle(PREFIX+'isfavorite',active);
    button.title=label;
    button.setAttribute('aria-label',label);
    button.setAttribute('aria-pressed',active?'true':'false');
    const caption=document.createElement('span');
    caption.textContent=T(active?'В избранном':'В избранное');
    button.replaceChildren(favoriteGlyph(active),caption);
  }
  async function toggleFavorite(item,button) {
    if(button.disabled)return;
    const owner=userId();
    const mediaId=itemId(item);
    if(!owner||!mediaId)return;
    const before=isFavorite(item);
    button.disabled=true;
    button.classList.add(PREFIX+'pending');
    try {
      const client=api();
      if(!client?.fetch || !client?.getUrl)throw new Error('Jellyfin API unavailable');
      const path='Users/'+encodeURIComponent(owner)+'/FavoriteItems/'+encodeURIComponent(mediaId);
      const result=await client.fetch({url:client.getUrl(path),type:before?'DELETE':'POST'});
      if(!result.ok)throw new Error('Jellyfin HTTP '+result.status);
      // Persist state in the current client's cached items until next normal refresh.
      const userData=get(item,'userData','UserData',{})||{};
      userData.IsFavorite=!before;
      userData.isFavorite=!before;
      item.UserData=userData;
      item.userData=userData;
      showFavoriteState(button,item);
    } catch (error) {
      console.warn('[JFToStashSync] Could not update media favorite:',error);
      button.title=T('Ошибка сохранения избранного: ')+(error?.message||String(error));
      button.setAttribute('aria-label',button.title);
      // Keep previous state if Jellyfin rejected the request.
    } finally {
      button.disabled=false;
      button.classList.remove(PREFIX+'pending');
    }
  }
  function playItem(item){
    const pid=itemId(item);
    // Delegate playback to Jellyfin when the web playback manager exists.
    try {
      const mgr=window.Emby?.PlaybackManager || window.playbackManager;
      if(mgr && typeof mgr.play==='function') {
        const result=mgr.play({ids:[pid]});
        if(result && typeof result.catch==='function') result.catch(()=>{location.hash=detailLink(item);});
        return;
      }
    } catch (_) { /* details page is a reliable fallback */ }
    location.hash=detailLink(item);
  }
  function draw(force){
    if(!state.enabled||!isHome()){unmount();return;}
    stylesOnce();
    const section=mount();if(!section)return;
    const item=state.items[state.current];
    if(!item){
      if(section.dataset.rendered==='empty')return;
      section.textContent='';const text=elt('div','empty',state.error||'Нет фильмов или сериалов с фоновыми изображениями.');
      section.appendChild(text);section.dataset.rendered='empty';stop();return;
    }
    const signature=id(itemId(item))+'/'+state.current;
    if(!force&&state.cardId===signature&&section.dataset.rendered===signature){
      // The previous DOM was detached rather than destroyed on route change.
      syncZoomPlayback();resumeTimer();
      return;
    }
    state.cardId=signature;section.dataset.rendered=signature;section.replaceChildren();
    const card=elt('div','card');card.dataset.itemId=itemId(item);
    card.style.setProperty('--'+PREFIX+'zoom-duration', durationMs()+'ms');
    const backdropImg=elt('img','backdrop');backdropImg.alt='';backdropImg.src=backdrop(item);
    backdropImg.addEventListener('error',()=>{if(state.items.length>1)slide(1);},{once:true});
    card.appendChild(backdropImg);card.appendChild(elt('div','shade'));
    preloadNeighbors();
    const content=elt('div','content');card.appendChild(content);
    const top=elt('div','top');content.appendChild(top);
    if(state.items.length>1){
      const nav=elt('div','nav');
      for(const [label,title,dir] of [['‹',T('Предыдущий'),-1],['›',T('Следующий'),1]]){
        const b=elt('button','btn',label);b.type='button';b.title=title;
        b.addEventListener('click',e=>{e.stopPropagation();slide(dir);});nav.appendChild(b);
      }
      top.appendChild(nav);
    }
    const counter=elt('span','counter',(state.current+1)+' / '+state.items.length);top.appendChild(counter);
    const body=elt('div','body');content.appendChild(body);
    const logoSlot=elt('div','logoSlot');body.appendChild(logoSlot);
    const image=logo(item);
    if(image){const img=elt('img','logo');img.alt='';img.src=image;logoSlot.appendChild(img);}
    const nameSlot=elt('div','nameSlot');body.appendChild(nameSlot);
    // The title has a reserved row even if the video does not have a Logo.
    const h=elt('h2','name',get(item,'name','Name',''));nameSlot.appendChild(h);
    const metaSlot=elt('div','metaSlot');body.appendChild(metaSlot);
    if(state.settings.ratings){
      const meta=elt('div','meta');
      const year=get(item,'productionYear','ProductionYear',null);
      const rating=get(item,'communityRating','CommunityRating',null);
      for(const value of [year,rating ? '★ '+Number(rating).toFixed(1):null]){
        if(value!==null&&value!==undefined) meta.appendChild(elt('span','',value));
      }
      const genres=get(item,'genres','Genres',[])||[];
      if(Array.isArray(genres)&&genres.length)meta.appendChild(elt('span','',genres.slice(0,2).join(' · ')));
      metaSlot.appendChild(meta);
    }
    const overviewSlot=elt('div','overviewSlot');body.appendChild(overviewSlot);
    if(state.settings.overview){
      const overview=String(get(item,'overview','Overview','')||'').replace(/<[^>]+>/g,'').trim();
      if(overview)overviewSlot.appendChild(elt('p','overview',overview));
    }
    const actions=elt('div','actions');
    const play=elt('button','btn','▶ Воспроизвести');play.type='button';play.addEventListener('click',()=>playItem(item));
    const details=elt('a','btn','Подробнее');details.href=detailLink(item);
    const favorite=elt('button','btn');
    favorite.type='button';
    favorite.classList.add(PREFIX+'favorite');
    showFavoriteState(favorite,item);
    favorite.addEventListener('click',()=>{toggleFavorite(item,favorite);});
    actions.appendChild(play);
    actions.appendChild(details);
    actions.appendChild(favorite);
    body.appendChild(actions);
    if(state.settings.progress && state.items.length>1){
      const prog=elt('div','progress');prog.setAttribute('aria-hidden','true');prog.appendChild(elt('div','progressfill'));card.appendChild(prog);
    }
    card.addEventListener('touchstart',e=>{state.startX=e.changedTouches?.[0]?.screenX||0;},{passive:true});
    card.addEventListener('touchend',e=>{const dx=(e.changedTouches?.[0]?.screenX||0)-state.startX;
      if(Math.abs(dx)>55)slide(dx<0?1:-1);},{passive:true});
    section.appendChild(card);schedule();syncZoomPlayback();
  }
  async function load(){
    if(state.running||Date.now()<state.exp||!state.user)return state.running;
    const token=++state.generation;
    // Distinguish an existing slideshow from a one-item progressive preview.
    // A preview from the first library must not pin the final slideshow to a
    // lower-rated item when another library has a higher-rated candidate.
    const hadSlides=state.items.length>0;
    state.running=(async()=>{
      try{
        const config=await (window.__jfHomeWarmup?.getConfig?.() || json('JFToStashSync/PlayerOCounterConfig'));
        if(token!==state.generation)return;
        state.enabled=!!get(config,'featuredMediaEnabled','FeaturedMediaEnabled',false);
        if(!state.enabled){state.items=[];state.exp=Date.now()+60000;unmount();return;}
        state.settings=defaults(config);
        const items=await loadItems(state.settings,token,preview => {
          if(token!==state.generation || hadSlides)return;
          const slides=Array.isArray(preview)?preview:[preview];
          if(!slides.length)return;
          state.items=slides;
          state.current=0;
          state.cardId='';
          window.__jfHomeWarmup?.preloadImages?.(slides.slice(0,3).flatMap(x=>[backdrop(x),logo(x)]));
          draw(true);
          window.__jfStashHomeLoader?.mark('media');
        });
        if(token!==state.generation)return;
        const currentId = state.items[state.current] ? id(itemId(state.items[state.current])) : '';
        state.items=items;
        window.__jfHomeWarmup?.preloadImages?.(items.slice(0,3).flatMap(x=>[backdrop(x),logo(x)]));
        const previousPosition = items.findIndex(item => id(itemId(item)) === currentId);
        state.current=hadSlides && previousPosition >= 0 ? previousPosition : 0;
        state.error='';state.cardId='';
        state.exp=Date.now()+CACHE_MS;draw(true);
      }catch(e){
        if(token!==state.generation)return;
        state.error=e?.message||T('Не удалось загрузить медиатеку');state.exp=Date.now()+30000;
        if(!state.items.length)draw(true);
        console.warn('[JFToStashSync] Featured media banner:',e);
      }finally{if(token===state.generation)state.running=null; window.__jfStashHomeLoader?.mark('media');}
    })();
    return state.running;
  }
  function scheduleMount(){
    if(mountPending)return;
    mountPending=true;
    queueMicrotask(()=>{
      mountPending=false;
      if(!isHome())unmount();
      else if(state.enabled && state.items.length)draw(false);
      // Direct navigation to Home from another Jellyfin page needs a first load,
      // not the 12-second periodic polling fallback.
      if(isHome() && Date.now()>=state.exp && !state.running)load();
    });
  }
  function poll(){
    const current=userId();
    if(current!==state.user){
      state.user=current;state.generation++;state.items=[];state.enabled=false;state.exp=0;state.running=null;
      stop();unmount();retainedSection=null;state.cardId='';
    }
    // Load even while the user is on Settings / Details; mounting remains Home-only.
    if(current && Date.now()>=state.exp && !state.running)load();
    if(!isHome()){unmount();return;}
    if(state.enabled && state.items.length)draw(false);
  }
  function start(){
    if(!document.body)return;
    state.user=userId();
    new MutationObserver(mutations=>{
      // Ignore changes inside our card; those must not restart the slide timer.
      if(mutations.some(m=>!m.target.closest?.('#'+ID)))scheduleMount();
    }).observe(document.body,{childList:true,subtree:true});
    // Jellyfin may replace the Home root after the route event. Attempt an
    // immediate remount and let the DOM observer retry when the host is created.
    window.addEventListener('hashchange',scheduleMount,true);
    window.addEventListener('popstate',scheduleMount,true);
    document.addEventListener('visibilitychange',()=>{
      if(document.visibilityState==='hidden')pauseTimer();
      else if(isHome()){if(Date.now()>=state.exp)load();else resumeTimer();}
      syncZoomPlayback();
    });
    window.addEventListener('keydown',e=>{
      if(e.defaultPrevented || e.altKey || e.ctrlKey || e.metaKey || !state.enabled ||
         state.items.length<2 || !isHome() || document.visibilityState==='hidden' ||
         !state.hovered ||
         ['INPUT','TEXTAREA','SELECT'].includes(document.activeElement?.tagName) ||
         document.activeElement?.isContentEditable)return;
      if(e.key==='ArrowLeft'||e.key==='ArrowRight'){e.preventDefault();slide(e.key==='ArrowLeft'?-1:1);}
    });
    document.addEventListener('jf-to-stash-user-ready',poll);
    poll();setInterval(poll,12000);
    const authWarmup=setInterval(()=>{if(userId()){poll();clearInterval(authWarmup);}},600);
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});
  else start();
})();
