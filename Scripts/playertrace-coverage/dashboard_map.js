// Client artwork, reference markers and authored points share ONE world-meter
// camera. Image bounds belong to that particular DDS, never to its viewport.
let mapAtlasData = null;
let activeMap = null;
let mapView = { x: 0, y: 0, scale: 1 };
let mapMode = 'inspect';
let mapInitPromise = null;
let selectedTarget = null;
let draftRoutePoints = [];
let selectedDraftIndex = -1;
let mapDrag = null;
const mapImages = new Map();
const mapHubs = new Map();
const mapPois = new Map();
let navHeatmapData = null;
const draftStorageKey = 'aaemu.world0.route-draft.v1';
const mapEl = id => document.getElementById(id);
const finiteMapNumber = n => typeof n === 'number' && Number.isFinite(n) && Math.abs(n) <= 1e7;
const mapRound = n => Math.round(n * 100) / 100;

function mapNotice(text) { mapEl('mapNotice').textContent = text; }

async function initWorldMap() {
    if (!mapInitPromise) {
        mapInitPromise = loadWorldMap().catch(error => {
            mapInitPromise = null;
            mapNotice(error.message);
        });
    }
    await mapInitPromise;
    resizeMapCanvas();
}

async function loadWorldMap() {
    const response = await fetch('/api/map/data');
    if (!response.ok) throw Error('Map data could not load (' + response.status + ').');
    mapAtlasData = await response.json();
    const manifest = mapAtlasData.map_manifest;
    if (!manifest?.maps?.length) throw Error(manifest?.error || 'Client maps have not been prepared.');
    for (const hub of mapAtlasData.junctions || []) mapHubs.set(hub.id, hub);
    for (const poi of mapAtlasData.pois || []) mapPois.set(poi.id, poi);
    const picker = mapEl('mapPicker');
    picker.replaceChildren();
    for (const kind of ['world', 'continent', 'zone']) {
        const group = document.createElement('optgroup');
        group.label = kind === 'zone' ? 'Detailed zone maps' : 'Overview maps';
        for (const m of manifest.maps.filter(m => m.kind === kind).sort((a,b) => a.title.localeCompare(b.title))) {
            group.append(new Option(m.title, m.key));
        }
        picker.append(group);
    }
    mapEl('mapProvenance').textContent = `${manifest.client} · DB md5 ${manifest.database_md5}. Local client artwork; no external map service required.`;
    setupMapEvents();
    new ResizeObserver(resizeMapCanvas).observe(mapEl('mapCanvasWrap'));
    resizeMapCanvas();
    chooseMap('world');
    restoreDraft();
}

function chooseMap(key) {
    const next = mapAtlasData?.map_manifest.maps.find(m => m.key === key);
    if (!next) return;
    activeMap = next;
    mapEl('mapPicker').value = key;
    mapEl('hudMap').textContent = next.title + ' · ' + next.bounds_source;
    if (!mapImages.has(key)) {
        const image = new Image();
        const entry = { image, ready: false, failed: false };
        mapImages.set(key, entry);
        image.onload = () => { entry.ready = true; redrawMap(); };
        image.onerror = () => { entry.failed = true; redrawMap(); };
        image.src = '/api/map/image?key=' + encodeURIComponent(key);
    }
    mapResetView();
}

function canvasSize() {
    const canvas = mapEl('worldMapCanvas');
    return { width: canvas.clientWidth, height: canvas.clientHeight };
}

function resizeMapCanvas() {
    const canvas = mapEl('worldMapCanvas'), wrap = mapEl('mapCanvasWrap');
    if (!wrap.clientWidth || !wrap.clientHeight) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(wrap.clientWidth * dpr);
    canvas.height = Math.round(wrap.clientHeight * dpr);
    redrawMap();
}

function worldToScreen(x, y) {
    const {width, height} = canvasSize();
    return { x: width/2 + (x-mapView.x)*mapView.scale, y: height/2 - (y-mapView.y)*mapView.scale };
}

function screenToWorld(x, y) {
    const {width, height} = canvasSize();
    return { x: mapView.x + (x-width/2)/mapView.scale, y: mapView.y - (y-height/2)/mapView.scale };
}

function fitMapBounds(b) {
    const {width, height} = canvasSize();
    mapView = { x: (b.min_x+b.max_x)/2, y: (b.min_y+b.max_y)/2,
        scale: Math.min(width/Math.max(100,b.max_x-b.min_x), height/Math.max(100,b.max_y-b.min_y)) * .94 };
    redrawMap();
}

function mapResetView() { if (activeMap) fitMapBounds(activeMap.bounds); }

function fitDraftRoute() {
    if (!draftRoutePoints.length) return;
    fitMapBounds({min_x: Math.min(...draftRoutePoints.map(p=>p.x)), max_x: Math.max(...draftRoutePoints.map(p=>p.x)),
        min_y: Math.min(...draftRoutePoints.map(p=>p.y)), max_y: Math.max(...draftRoutePoints.map(p=>p.y))});
}

function mapZoom(factor, anchor) {
    const {width, height} = canvasSize();
    anchor ||= {x:width/2, y:height/2};
    const before = screenToWorld(anchor.x, anchor.y);
    mapView.scale = Math.max(.002, Math.min(12, mapView.scale*factor));
    const after = screenToWorld(anchor.x, anchor.y);
    mapView.x += before.x-after.x;
    mapView.y += before.y-after.y;
    redrawMap();
}

function mapPointer(event) {
    const rect = mapEl('worldMapCanvas').getBoundingClientRect();
    return { x: event.clientX-rect.left, y: event.clientY-rect.top };
}

async function toggleHeatmapLayer() {
    if (mapEl('layerHeatmap').checked && !navHeatmapData) {
        try {
            const resp = await fetch('/api/map/nav-telemetry');
            if (resp.ok) navHeatmapData = await resp.json();
        } catch (e) {
            console.warn('Failed to load nav telemetry:', e);
        }
    }
    redrawMap();
}

function nearestMapTarget(point) {
    let nearest = null, distance = 12 / mapView.scale;
    if (mapEl('layerPois')?.checked) {
        for (const poi of mapPois.values()) {
            const d = Math.hypot(poi.x - point.x, poi.y - point.y);
            if (d < distance) { distance = d; nearest = { ...poi, isPoi: true }; }
        }
    }
    if (!nearest && mapEl('layerRoads')?.checked) {
        for (const hub of mapHubs.values()) {
            const d = Math.hypot(hub.x - point.x, hub.y - point.y);
            if (d < distance) { distance = d; nearest = hub; }
        }
    }
    return nearest;
}

function setupMapEvents() {
    const canvas = mapEl('worldMapCanvas');
    canvas.addEventListener('pointerdown', event => {
        if (event.button !== 0 || mapDrag) return;
        canvas.focus({preventScroll:true});
        canvas.setPointerCapture(event.pointerId);
        mapDrag = { id:event.pointerId, start:mapPointer(event), view:{...mapView}, moved:false };
    });
    canvas.addEventListener('pointermove', event => {
        const p = mapPointer(event);
        if (mapDrag?.id === event.pointerId) {
            const dx = p.x-mapDrag.start.x, dy = p.y-mapDrag.start.y;
            if (Math.hypot(dx,dy) > 5) mapDrag.moved = true;
            if (mapDrag.moved) {
                mapView.x = mapDrag.view.x-dx/mapView.scale;
                mapView.y = mapDrag.view.y+dy/mapView.scale;
                redrawMap();
            }
        }
        const wp = screenToWorld(p.x,p.y), hub = nearestMapHub(wp);
        mapEl('hudCoords').textContent = `X ${wp.x.toFixed(2)} · Y ${wp.y.toFixed(2)}`;
        mapEl('hudElev').textContent = hub ? `Nearby hub Z ${hub.z.toFixed(2)} (only at hub)` : 'Z unknown';
    });
    canvas.addEventListener('pointerup', event => {
        if (mapDrag?.id !== event.pointerId) return;
        const drag = mapDrag;
        mapDrag = null;
        canvas.releasePointerCapture(event.pointerId);
        const p = mapPointer(event), size = canvasSize();
        if (drag.moved || p.x < 0 || p.y < 0 || p.x > size.width || p.y > size.height) return;
        const target = (mapEl('snapHubs').checked || mapEl('layerPois')?.checked) ? nearestMapTarget(wp) : null;
        const point = target ? {
            x: target.x, y: target.y, z: target.z,
            source: target.isPoi ? target.type : 'reference-hub',
            label: target.label || target.id,
            desc: target.what || ''
        } : {x:mapRound(wp.x),y:mapRound(wp.y),z:null,source:'map-click'};
        if (mapMode === 'draft') addDraftPoint(point);
        else selectMapPoint(point);
    });
    for (const name of ['pointercancel','lostpointercapture']) canvas.addEventListener(name, event => {
        if (mapDrag?.id === event.pointerId) mapDrag = null;
    });
    canvas.addEventListener('wheel', event => {
        event.preventDefault();
        mapZoom(event.deltaY < 0 ? 1.2 : 1/1.2, mapPointer(event));
    }, {passive:false});
    canvas.addEventListener('keydown', event => {
        const directions = {ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,1],ArrowDown:[0,-1]};
        if (directions[event.key]) {
            event.preventDefault();
            mapView.x += directions[event.key][0]*60/mapView.scale;
            mapView.y += directions[event.key][1]*60/mapView.scale;
            redrawMap();
        } else if (['+','=','-'].includes(event.key)) {
            event.preventDefault(); mapZoom(event.key === '-' ? 1/1.4 : 1.4);
        }
    });
}

function redrawMap() {
    if (!activeMap || !mapEl('worldMapCanvas').clientWidth) return;
    const canvas = mapEl('worldMapCanvas'), ctx = canvas.getContext('2d');
    const {width,height} = canvasSize();
    ctx.setTransform(canvas.width/width,0,0,canvas.height/height,0,0);
    ctx.clearRect(0,0,width,height);
    const b = activeMap.bounds, top = worldToScreen(b.min_x,b.max_y), bottom = worldToScreen(b.max_x,b.min_y);
    const entry = mapImages.get(activeMap.key);
    if (mapEl('layerArt').checked) {
        if (entry?.ready) ctx.drawImage(entry.image,top.x,top.y,bottom.x-top.x,bottom.y-top.y);
        else {
            ctx.fillStyle = '#f5c66d'; ctx.font = '14px sans-serif';
            ctx.fillText(entry?.failed ? 'Client artwork unavailable. Re-run prepare_map_assets.py.' : 'Loading client artwork…',18,30);
        }
    }
    const inView = p => p.x >= -10 && p.y >= -10 && p.x <= width+10 && p.y <= height+10;
    if (mapEl('layerGrid').checked) drawMapGrid(ctx,width,height);
    if (mapEl('layerZones').checked) {
        ctx.strokeStyle = '#24648c'; ctx.lineWidth = 1;
        for (const m of mapAtlasData.map_manifest.maps.filter(m=>m.kind==='zone')) {
            const tl = worldToScreen(m.bounds.min_x,m.bounds.max_y), br = worldToScreen(m.bounds.max_x,m.bounds.min_y);
            ctx.strokeRect(tl.x,tl.y,br.x-tl.x,br.y-tl.y);
        }
    }
    if (mapEl('layerLinks').checked) {
        ctx.strokeStyle = 'rgba(129,68,6,.6)'; ctx.lineWidth = 1; ctx.setLineDash([4,5]);
        ctx.beginPath();
        for (const edge of mapAtlasData.edges || []) {
            const a=mapHubs.get(edge.from), b=mapHubs.get(edge.to);
            if (!a || !b) continue;
            const sa=worldToScreen(a.x,a.y), sb=worldToScreen(b.x,b.y);
            // Canvas clipping also preserves crossings with both endpoints offscreen.
            ctx.moveTo(sa.x,sa.y); ctx.lineTo(sb.x,sb.y);
        }
        ctx.stroke(); ctx.setLineDash([]);
    }
    if (mapEl('layerRoads').checked) {
        for (const hub of mapHubs.values()) {
            const s=worldToScreen(hub.x,hub.y);
            if (!inView(s)) continue;
            ctx.beginPath(); ctx.arc(s.x,s.y,3.5,0,2*Math.PI);
            ctx.fillStyle='#ffdf77'; ctx.strokeStyle='#52330e'; ctx.lineWidth=1.5; ctx.fill(); ctx.stroke();
        }
    }
    if (mapEl('layerPois')?.checked) {
        for (const poi of mapPois.values()) {
            const s = worldToScreen(poi.x, poi.y);
            if (!inView(s)) continue;
            ctx.beginPath();
            if (poi.type === 'illegal_tree_farm') {
                // Emerald diamond for illegal tree farm
                const size = 6;
                ctx.moveTo(s.x, s.y - size);
                ctx.lineTo(s.x + size, s.y);
                ctx.lineTo(s.x, s.y + size);
                ctx.lineTo(s.x - size, s.y);
                ctx.closePath();
                ctx.fillStyle = '#10b981'; ctx.strokeStyle = '#064e3b'; ctx.lineWidth = 1.8;
                ctx.fill(); ctx.stroke();
                if (mapView.scale > 0.05) {
                    ctx.font = 'bold 10px sans-serif'; ctx.textAlign = 'center'; ctx.fillStyle = '#6ee7b7';
                    ctx.fillText('🌲 ' + (poi.label || 'Wild Farm'), s.x, s.y - 8);
                }
            } else {
                ctx.arc(s.x, s.y, 4.5, 0, 2 * Math.PI);
                ctx.fillStyle = '#f59e0b'; ctx.strokeStyle = '#78350f'; ctx.lineWidth = 1.5;
                ctx.fill(); ctx.stroke();
            }
        }
    }
    if (mapEl('layerHeatmap')?.checked && navHeatmapData) {
        // Render desire corridors (orange)
        for (const c of navHeatmapData.corridors || []) {
            const s = worldToScreen(c.x, c.y);
            if (!inView(s)) continue;
            ctx.beginPath(); ctx.arc(s.x, s.y, 5, 0, 2 * Math.PI);
            ctx.fillStyle = `rgba(249, 115, 22, ${Math.min(0.85, Math.max(0.2, c.intensity * 2))})`;
            ctx.fill();
        }
        // Render stall & obstacle hotspots (pulsing red)
        for (const h of navHeatmapData.hotspots || []) {
            const s = worldToScreen(h.x, h.y);
            if (!inView(s)) continue;
            ctx.beginPath(); ctx.arc(s.x, s.y, 8, 0, 2 * Math.PI);
            ctx.fillStyle = 'rgba(239, 68, 68, 0.4)'; ctx.strokeStyle = '#ef4444'; ctx.lineWidth = 2;
            ctx.fill(); ctx.stroke();
            ctx.font = '9px sans-serif'; ctx.textAlign = 'center'; ctx.fillStyle = '#fff';
            ctx.fillText(h.events, s.x, s.y + 3);
        }
    }
    if (draftRoutePoints.length) {
        ctx.beginPath();
        draftRoutePoints.forEach((p,i) => { const s=worldToScreen(p.x,p.y); if (i) ctx.lineTo(s.x,s.y); else ctx.moveTo(s.x,s.y); });
        ctx.strokeStyle='#251128'; ctx.lineWidth=6; ctx.stroke();
        ctx.strokeStyle='#ff55c4'; ctx.lineWidth=3; ctx.stroke();
        draftRoutePoints.forEach((p,i) => {
            const s=worldToScreen(p.x,p.y); if (!inView(s)) return;
            ctx.beginPath(); ctx.arc(s.x,s.y,i===selectedDraftIndex ? 9 : 7,0,2*Math.PI);
            ctx.fillStyle=p.z===null ? '#522e07' : '#822866'; ctx.fill();
            ctx.strokeStyle=p.z===null ? '#ffca65' : '#ff92dc'; ctx.lineWidth=2; ctx.stroke();
            ctx.font='bold 11px sans-serif'; ctx.textAlign='center'; ctx.fillStyle='#fff'; ctx.fillText(i+1,s.x,s.y-12);
        });
    }
    if (selectedTarget && mapMode==='inspect') {
        const s=worldToScreen(selectedTarget.x,selectedTarget.y);
        ctx.strokeStyle='#f43f5e'; ctx.lineWidth=2;
        ctx.beginPath(); ctx.arc(s.x,s.y,10,0,2*Math.PI);
        ctx.moveTo(s.x-18,s.y); ctx.lineTo(s.x+18,s.y); ctx.moveTo(s.x,s.y-18); ctx.lineTo(s.x,s.y+18); ctx.stroke();
    }
    ctx.textAlign='left';
    ctx.fillStyle='rgba(8,12,20,.85)'; ctx.fillRect(10,height-32,240,24);
    ctx.fillStyle='#e4eaf0'; ctx.font='12px monospace';
    ctx.fillText('N ↑  ·  Drag: pan  ·  Scroll: zoom',18,height-16);
}

function drawMapGrid(ctx,width,height) {
    const tl=screenToWorld(0,0), br=screenToWorld(width,height);
    const step=10**Math.ceil(Math.log10(100/mapView.scale));
    ctx.strokeStyle='rgba(20,40,60,.35)'; ctx.fillStyle='#182637'; ctx.font='10px monospace'; ctx.textAlign='left'; ctx.lineWidth=1;
    for (let x=Math.ceil(tl.x/step)*step;x<=br.x;x+=step) {
        const s=worldToScreen(x,0); ctx.beginPath(); ctx.moveTo(s.x,0); ctx.lineTo(s.x,height); ctx.stroke(); ctx.fillText(x+'m',s.x+3,14);
    }
    for (let y=Math.ceil(br.y/step)*step;y<=tl.y;y+=step) {
        const s=worldToScreen(0,y); ctx.beginPath(); ctx.moveTo(0,s.y); ctx.lineTo(width,s.y); ctx.stroke(); ctx.fillText(y+'m',3,s.y-3);
    }
}

function selectMapPoint(point) {
    selectedTarget = {...point};
    for (const axis of ['x','y','z']) mapEl('inspect'+axis.toUpperCase()).value = point[axis] ?? '';
    mapEl('inspectName').textContent = point.label || 'World coordinate';
    if (point.source === 'illegal_tree_farm') {
        mapEl('inspectSub').textContent = '🌲 ' + (point.desc || 'Secret Wild Tree Farm · Secluded wilderness ideal for illegal saplings & thunderstruck tree hunts.');
    } else if (point.source === 'shipwreck') {
        mapEl('inspectSub').textContent = '⚓ ' + (point.desc || 'Sunken Shipwreck POI.');
    } else if (point.source === 'reference-hub') {
        mapEl('inspectSub').textContent = 'Reference hub XYZ; candidate, not movement-validated.';
    } else {
        mapEl('inspectSub').textContent = 'World 0 · Z must come from measurement, not map artwork.';
    }
    redrawMap();
}

function enteredMapPoint() {
    const values = ['X','Y','Z'].map(a=>mapEl('inspect'+a).value.trim());
    const point={x:Number(values[0]),y:Number(values[1]),z:values[2]==='' ? null : Number(values[2]),source:'operator'};
    if (!values[0] || !values[1] || !validDraftPoint(point)) throw Error('Enter finite X and Y. Leave Z blank only for an unfinished draft.');
    return point;
}

function focusEnteredPoint() {
    try {
        const p=enteredMapPoint(); selectMapPoint(p);
        // Choose the tightest detailed map containing this coordinate. These are
        // image extents only; no inference of the runtime ZoneId is made.
        const candidates=mapAtlasData.map_manifest.maps.filter(m=>m.kind==='zone' && p.x>=m.bounds.min_x && p.x<=m.bounds.max_x && p.y>=m.bounds.min_y && p.y<=m.bounds.max_y);
        candidates.sort((a,b)=>(a.bounds.max_x-a.bounds.min_x)*(a.bounds.max_y-a.bounds.min_y)-(b.bounds.max_x-b.bounds.min_x)*(b.bounds.max_y-b.bounds.min_y));
        if (candidates.length) chooseMap(candidates[0].key);
        mapView.x=p.x; mapView.y=p.y; redrawMap(); mapNotice('Located in world 0. Confirm this is your current runtime world.');
    } catch(error) { mapNotice(error.message); }
}

function addEnteredPoint() {
    try { const p=enteredMapPoint(); setMapMode('draft'); addDraftPoint(p); }
    catch(error) { mapNotice(error.message); }
}

async function copyMapText(text) {
    mapEl('mapOutput').value=text;
    try { await navigator.clipboard.writeText(text); mapNotice('Copied. Also available in the preview below.'); }
    catch { mapNotice('Clipboard unavailable on this connection. Select and copy the preview below.'); mapEl('mapOutput').focus(); mapEl('mapOutput').select(); }
}

function copySelectedPosition() {
    try { const p=enteredMapPoint(); copyMapText(JSON.stringify({worldId:0,x:p.x,y:p.y,z:p.z})); }
    catch(error) { mapNotice(error.message); }
}

function copyMoveCommand() {
    try {
        const p=enteredMapPoint();
        if (p.z===null) throw Error('A measured Z is required for /move. No height has been guessed.');
        copyMapText(`/move ${p.x} ${p.y} ${p.z}`);
    } catch(error) { mapNotice(error.message); }
}

function setMapMode(mode) {
    mapMode=mode;
    mapEl('mapModeInspectBtn').classList.toggle('active',mode==='inspect');
    mapEl('mapModeDraftBtn').classList.toggle('active',mode==='draft');
    mapEl('sidebarInspectPanel').style.display=mode==='inspect' ? 'block':'none';
    mapEl('sidebarDraftPanel').style.display=mode==='draft' ? 'block':'none';
    mapEl('sidebarHeaderTitle').textContent=mode==='inspect' ? 'Map inspector':'Route builder';
    mapNotice(mode==='draft' ? 'Click to add. Dragging never creates a waypoint. Amber points need Z.' : '');
    redrawMap();
}

function validDraftPoint(p) {
    return p && finiteMapNumber(p.x) && finiteMapNumber(p.y) && (p.z===null || finiteMapNumber(p.z));
}

function routeName() {
    const name=mapEl('routeName').value.trim();
    if (!/^[A-Za-z0-9_-]{1,80}$/.test(name)) throw Error('Use 1–80 letters, digits, underscores or hyphens for the route name.');
    return name;
}

function draftDocument() {
    return {schema:'aaemu.world0.route-draft.v1',worldId:0,name:mapEl('routeName').value,points:draftRoutePoints};
}

function saveDraft() {
    try { localStorage.setItem(draftStorageKey,JSON.stringify(draftDocument())); }
    catch { mapNotice('Browser storage unavailable. Use Save draft before leaving.'); }
    updateDraftSummary();
}

function restoreDraft() {
    try {
        const text=localStorage.getItem(draftStorageKey);
        if (text) applyImportedRoute(JSON.parse(text));
        else updateDraftSidebar();
    } catch { mapNotice('Saved draft could not be restored; it has not been overwritten.'); }
}

function addDraftPoint(p) {
    if (draftRoutePoints.length>=5000) { mapNotice('Maximum 5,000 waypoints per draft.'); return; }
    draftRoutePoints.push(p); selectedDraftIndex=draftRoutePoints.length-1;
    updateDraftSidebar(); saveDraft(); redrawMap();
}

function updateDraftSummary() {
    let horizontal=0, longest=0;
    for (let i=1;i<draftRoutePoints.length;i++) {
        const d=Math.hypot(draftRoutePoints[i].x-draftRoutePoints[i-1].x,draftRoutePoints[i].y-draftRoutePoints[i-1].y);
        horizontal+=d; longest=Math.max(longest,d);
    }
    const unknown=draftRoutePoints.filter(p=>p.z===null).length;
    mapEl('draftCount').textContent=draftRoutePoints.length+' waypoints';
    mapEl('draftDistance').textContent=`${horizontal.toFixed(1)} m horizontal · longest leg ${longest.toFixed(1)} m`;
    let reason='';
    try { routeName(); } catch(error) { reason=error.message; }
    if (!reason && draftRoutePoints.length<2) reason='Add at least two points.';
    if (!reason && unknown) reason=`${unknown} waypoint(s) need measured Z. Draft saving is still available.`;
    mapEl('exportMapperBtn').disabled=!!reason;
    mapEl('draftValidation').textContent=reason || 'XYZ complete. Walkability and height still require in-game verification.';
    mapEl('mapperPlayHint').textContent='/mapper play <bot_name> '+mapEl('routeName').value;
}

function updateDraftSidebar() {
    const list=mapEl('draftPoints'); list.replaceChildren();
    draftRoutePoints.forEach((point,index) => {
        const row=document.createElement('div'); row.className='draft-point'+(index===selectedDraftIndex ? ' active':'');
        const title=document.createElement('button'); title.className='map-btn'; title.textContent=`${index+1}. ${point.label || point.source || 'Waypoint'}`;
        title.onclick=()=>{selectedDraftIndex=index;mapView.x=point.x;mapView.y=point.y;redrawMap();updateDraftSidebar();}; row.append(title);
        const fields=document.createElement('div');fields.className='map-coordinate-fields';
        for (const axis of ['x','y','z']) {
            const label=document.createElement('label');label.textContent=axis.toUpperCase();
            const input=document.createElement('input');input.type='number';input.step='any';input.className='form-control';
            input.value=point[axis] ?? '';input.placeholder='Unknown';input.setAttribute('aria-label',`Waypoint ${index+1} ${axis.toUpperCase()}`);
            input.onchange=()=>{
                const value=input.value.trim()==='' ? null : Number(input.value);
                if ((axis!=='z' && value===null) || (value!==null && !finiteMapNumber(value))) {
                    input.value=point[axis] ?? '';mapNotice('Enter a finite coordinate. Only Z may be unknown.');return;
                }
                point[axis]=value;point.source='operator';selectedDraftIndex=index;saveDraft();redrawMap();
            };
            label.append(input);fields.append(label);
        }
        row.append(fields);
        const remove=document.createElement('button');remove.className='map-btn';remove.textContent='Remove';
        remove.onclick=()=>{draftRoutePoints.splice(index,1);selectedDraftIndex=-1;updateDraftSidebar();saveDraft();redrawMap();};row.append(remove);
        list.append(row);
    });
    updateDraftSummary();
}

function undoDraftPoint() {
    draftRoutePoints.pop();selectedDraftIndex=-1;updateDraftSidebar();saveDraft();redrawMap();
}

function clearDraftRoute() {
    if (draftRoutePoints.length && !confirm('Clear this draft? Save it first if you want to keep it.')) return;
    draftRoutePoints=[];selectedDraftIndex=-1;updateDraftSidebar();saveDraft();redrawMap();
}

function downloadMapJson(value,filename) {
    const text=JSON.stringify(value,null,2);
    mapEl('mapOutput').value=text;
    const url=URL.createObjectURL(new Blob([text+'\n'],{type:'application/json'}));
    const link=document.createElement('a');link.href=url;link.download=filename;document.body.append(link);link.click();link.remove();
    setTimeout(()=>URL.revokeObjectURL(url),1000);
}

function downloadDraft() {
    try { downloadMapJson(draftDocument(),routeName()+'.draft.json');mapNotice('Draft downloaded; unknown heights are preserved as null.'); }
    catch(error) { mapNotice(error.message); }
}

function buildMapperRoute() {
    const name=routeName();
    if (draftRoutePoints.length<2 || draftRoutePoints.some(p=>!validDraftPoint(p) || p.z===null)) throw Error('At least two finite XYZ waypoints are required.');
    let distance=0;
    for (let i=1;i<draftRoutePoints.length;i++) {
        const a=draftRoutePoints[i-1],b=draftRoutePoints[i];distance+=Math.hypot(b.x-a.x,b.y-a.y,b.z-a.z);
    }
    // Exact MapperRouteData contract consumed by DevMapperService.GetRoute.
    return {RouteName:name,Author:'Dashboard route author',CreatedAt:new Date().toISOString(),TotalDistance:distance,
        WaypointCount:draftRoutePoints.length,ActionCount:0,
        Actions:draftRoutePoints.map((p,i)=>({ActionType:'Waypoint',X:p.x,Y:p.y,Z:p.z,Yaw:0,Label:p.label || `Waypoint ${i+1}`}))};
}

function exportMapperRoute() {
    try { const route=buildMapperRoute();downloadMapJson(route,route.RouteName+'.json');mapNotice('Downloaded MapperRouteData. Install in Game Data/Routes; replay is not automatically started.'); }
    catch(error) { mapNotice(error.message); }
}

function applyImportedRoute(data) {
    let points,name;
    if (data.schema==='aaemu.world0.route-draft.v1' && data.worldId===0) {points=data.points;name=data.name;}
    else if (Array.isArray(data.Actions)) {
        if (data.Actions.some(a=>a.ActionType!=='Waypoint' && a.ActionType!==0)) throw Error('This route contains non-waypoint actions. Import refused to preserve those actions.');
        points=data.Actions.map(a=>({x:a.X,y:a.Y,z:a.Z,label:a.Label,source:'imported-mapper'}));name=data.RouteName;
    } else throw Error('Expected a world-0 dashboard draft or waypoint-only MapperRouteData JSON.');
    if (typeof name!=='string' || !/^[A-Za-z0-9_-]{1,80}$/.test(name) || !Array.isArray(points) || points.length>5000 || points.some(p=>!validDraftPoint(p))) throw Error('Invalid route name or waypoint coordinates (maximum 5,000 points).');
    draftRoutePoints=points.map(p=>({x:p.x,y:p.y,z:p.z,source:typeof p.source==='string' ? p.source:'imported',label:typeof p.label==='string' ? p.label.slice(0,120):undefined}));
    mapEl('routeName').value=name;selectedDraftIndex=-1;updateDraftSidebar();redrawMap();
}

async function importRoute(input) {
    try {
        const file=input.files[0];if (!file) return;
        if (file.size>5*1024*1024) throw Error('Route file is too large (5 MiB maximum).');
        const data=JSON.parse(await file.text());
        if (draftRoutePoints.length && !confirm('Replace the current draft with this file?')) return;
        applyImportedRoute(data);saveDraft();setMapMode('draft');fitDraftRoute();
        mapNotice('Loaded. Mapper files have no world field: confirm the source was recorded in main_world (world 0).');
    } catch(error) { mapNotice(error.message); }
    finally {input.value='';}
}
