'use strict';

/* 壁纸穿透插件
   - 静态模式：读系统静态壁纸，按窗口位置裁切
   - 屏幕取景（实验）：捕获桌面（内容保护排除便签自己），支持动态壁纸
   - 性能：rAF 只在坐标变化时写一次 transform；收起/隐藏时停掉 rAF、定时器和捕获 */

(function () {
  const host = window.deskPluginHost;
  if (!host) return;

  let layer = null;
  let img = null;
  let video = null;
  let stream = null;
  let timerDisplay = null;
  let timerWall = null;
  let raf = null;
  let obs = null;

  let display = null;
  let displayAt = 0;
  let wallpaper = { path: '', style: 10, tile: false, mtime: 0 };
  let natural = null;
  let mode = 'static';
  let low = false;
  let weInfo = null;
  let weTimer = null;
  let suspended = false;
  let warned = false;
  let lastMove = '';
  let lastSize = '';
  let rolling = false;

  function onRoll(e) {
    rolling = !!(e && e.detail && e.detail.rolling);
    if (!video) return;
    if (rolling) video.pause();
    else if (mode === 'engine' || mode === 'capture') video.play().catch(() => {});
  }

  const toFileUrl = (p) => 'file:///' + encodeURI(String(p).replace(/\\/g, '/'));
  const log = (...a) => console.log('[wallpaper]', ...a);
  const toast = (msg) => typeof window.showToast === 'function' && window.showToast(msg, 3600);

  function opts() {
    const direct = window.__deskPluginOptions;
    if (direct) {
      return { engine: !!direct.engine, capture: !!direct.capture, low: !!direct.low };
    }
    const s = (host.getSettings && host.getSettings()) || {};
    const p = s.plugins || {};
    return {
      engine: !!p.wallpaperEngine,
      capture: !!p.wallpaperCapture,
      low: !!p.wallpaperLowPower,
    };
  }

  const loadSize = (url) =>
    new Promise((resolve) => {
      const i = new Image();
      i.onload = () => resolve({ w: i.naturalWidth, h: i.naturalHeight });
      i.onerror = () => resolve(null);
      i.src = url;
    });

  async function refreshDisplay(force) {
    if (!force && display && Date.now() - displayAt < 2000) return;
    displayAt = Date.now();
    try {
      display = await window.desk.getDisplay();
    } catch {
      display = null;
    }
    lastSize = '';
    lastMove = '';
  }

  /* ---------------- 静态壁纸 ---------------- */

  async function refreshWallpaper() {
    if (mode === 'engine') return;
    let info = null;
    try {
      info = await window.desk.getWallpaper();
    } catch {
      info = null;
    }
    if (!info) return;
    if (info.path === wallpaper.path && info.mtime === wallpaper.mtime) return;

    wallpaper = info;
    lastMove = '';
    lastSize = '';

    if (!info.path) {
      natural = null;
      if (img) img.removeAttribute('src');
      layer.hidden = true;
      document.body.classList.remove('desktop-wallpaper');
      if (!warned) {
        warned = true;
        toast(window.i18n ? window.i18n.t('plugin.noWallpaper') : 'No static wallpaper');
      }
      return;
    }

    natural = await loadSize(toFileUrl(info.path));
    if (!natural) {
      layer.hidden = true;
      document.body.classList.remove('desktop-wallpaper');
      return;
    }
    layer.hidden = false;
    document.body.classList.add('desktop-wallpaper');
    if (!img) {
      img = document.createElement('img');
      img.className = 'wallpaper-img';
      img.draggable = false;
      img.decoding = 'async';
      layer.appendChild(img);
    }
    img.src = toFileUrl(info.path);
    render(true);
  }

  function renderStatic(force) {
    if (!img || !natural || !wallpaper.path) return;
    const W = display.bounds.width;
    const H = display.bounds.height;
    const ox = Math.round(window.screenX - display.bounds.x);
    const oy = Math.round(window.screenY - display.bounds.y);

    if (wallpaper.tile) {
      const key = `${ox},${oy}`;
      if (key === lastMove && !force) return;
      lastMove = key;
      layer.style.backgroundImage = `url("${toFileUrl(wallpaper.path)}")`;
      layer.style.backgroundRepeat = 'repeat';
      layer.style.backgroundSize = 'auto';
      layer.style.backgroundPosition = `${-ox}px ${-oy}px`;
      img.style.display = 'none';
      return;
    }

    const iw = natural.w;
    const ih = natural.h;
    let dw;
    let dh;
    let dx;
    let dy;
    if (wallpaper.style === 2) {
      dw = W; dh = H; dx = 0; dy = 0;
    } else if (wallpaper.style === 6) {
      const s = Math.min(W / iw, H / ih);
      dw = iw * s; dh = ih * s; dx = (W - dw) / 2; dy = (H - dh) / 2;
    } else if (wallpaper.style === 0 || wallpaper.style === 1) {
      dw = iw; dh = ih; dx = (W - dw) / 2; dy = (H - dh) / 2;
    } else {
      const s = Math.max(W / iw, H / ih);
      dw = iw * s; dh = ih * s; dx = (W - dw) / 2; dy = (H - dh) / 2;
    }

    const sizeKey = `${Math.round(dw)}x${Math.round(dh)}`;
    if (sizeKey !== lastSize || force) {
      lastSize = sizeKey;
      img.style.display = 'block';
      img.style.width = `${Math.round(dw)}px`;
      img.style.height = `${Math.round(dh)}px`;
    }
    const moveKey = `${ox},${oy}`;
    if (moveKey === lastMove && !force) return;
    lastMove = moveKey;
    img.style.transform = `translate3d(${Math.round(dx - ox)}px, ${Math.round(dy - oy)}px, 0)`;
  }

  /* ---------------- 屏幕取景 ---------------- */

  /* ---------------- Wallpaper Engine 项目 ---------------- */

  async function startEngine() {
    const info = await window.desk.getWallpaperEngine();
    if (!info) throw new Error('no-we');
    weInfo = info;
    if (timerWall) {
      clearInterval(timerWall);
      timerWall = null;
    }
    layer.hidden = false;
    document.body.classList.add('desktop-wallpaper');

    if (info.video) {
      mode = 'engine';
      if (!video) {
        video = document.createElement('video');
        video.className = 'wallpaper-video';
        video.muted = true;
        video.loop = true;
        video.autoplay = true;
        video.playsInline = true;
        layer.appendChild(video);
      }
      video.src = toFileUrl(info.video);
      await video.play().catch(() => {});
      render(true);
      log('engine video: ' + info.file);
    } else if (info.preview) {
      mode = 'static';
      wallpaper = { path: info.preview, style: 10, tile: false, mtime: info.mtime };
      natural = await loadSize(toFileUrl(info.preview));
      if (!natural) throw new Error('preview load failed');
      if (!img) {
        img = document.createElement('img');
        img.className = 'wallpaper-img';
        img.draggable = false;
        img.decoding = 'async';
        layer.appendChild(img);
      }
      img.src = toFileUrl(info.preview);
      render(true);
      log('engine preview: ' + info.preview);
    } else {
      throw new Error('no preview');
    }

    if (!weTimer) {
      weTimer = setInterval(async () => {
        try {
          const now = await window.desk.getWallpaperEngine();
          if (!now) return;
          if (weInfo && now.file === weInfo.file && now.mtime === weInfo.mtime) return;
          stopEngine();
          await startEngine();
        } catch {}
      }, 5000);
    }
  }

  function stopEngine() {
    if (weTimer) clearInterval(weTimer);
    weTimer = null;
    weInfo = null;
    if (video) {
      video.pause();
      video.removeAttribute('src');
      video.remove();
      video = null;
    }
  }

  // 优先用官方 getDisplayMedia（主进程授权选屏）；不支持时退回旧的 desktop 约束
  async function openStream(src, capW, capH) {
    const fps = low ? 10 : 15;
    if (navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia) {
      try {
        const s = await navigator.mediaDevices.getDisplayMedia({
          audio: false,
          video: { frameRate: { ideal: fps, max: fps } },
        });
        log('getDisplayMedia ok');
        return s;
      } catch (e) {
        log('getDisplayMedia failed: ' + e.name + ' ' + e.message);
      }
    }
    log('fallback to chromeMediaSource');
    return navigator.mediaDevices.getUserMedia({
      audio: false,
      video: {
        mandatory: {
          chromeMediaSource: 'desktop',
          chromeMediaSourceId: src.id,
          maxWidth: capW,
          maxHeight: capH,
          maxFrameRate: fps,
        },
      },
    });
  }

  async function startCapture() {
    const src = await window.desk.getCaptureSource();
    if (!src) throw new Error('no capture source');
    const ok = await window.desk.setContentProtection(true);
    if (!ok) throw new Error('content protection unavailable');

    const d = await window.desk.getDisplay();
    const capW = low ? 1280 : Math.min(1920, Math.round(d.bounds.width));
    const capH = Math.max(1, Math.round((capW * d.bounds.height) / d.bounds.width));

    stream = await openStream(src, capW, capH);

    if (!video) {
      video = document.createElement('video');
      video.className = 'wallpaper-video';
      video.autoplay = true;
      video.muted = true;
      video.playsInline = true;
      video.onended = () => fallback();
      layer.appendChild(video);
    }
    video.srcObject = stream;
    await video.play().catch(() => {});
    mode = 'capture';
    lastSize = '';
    lastMove = '';
    render(true);
    log('capture started low=' + low);
  }

  function pauseCapture() {
    if (video) video.pause();
  }

  function resumeCapture() {
    if (video && stream) {
      video.play().catch(() => {});
      return;
    }
    if (mode === 'capture') startCapture().catch(() => fallback());
  }

  function stopCapture(keepProtection) {
    if (stream) {
      stream.getTracks().forEach((t) => t.stop());
      stream = null;
    }
    if (video) {
      video.pause();
      video.srcObject = null;
      video.remove();
      video = null;
    }
    if (!keepProtection) window.desk.setContentProtection(false);
  }

  async function fallback(kind) {
    if (kind === 'engine') {
      toast(window.i18n ? window.i18n.t('plugin.engineFail') : 'no wallpaper engine');
      stopEngine();
    } else if (mode === 'capture') {
      toast(window.i18n ? window.i18n.t('plugin.captureFail') : 'capture failed');
      stopCapture(false);
    }
    mode = 'static';
    img = null;
    await refreshWallpaper();
    if (!timerWall) timerWall = setInterval(refreshWallpaper, 3000);
  }

  function render(force) {
    if (!layer || layer.hidden || !display) return;
    if (mode === 'engine' && video) {
      const W = display.bounds.width;
      const H = display.bounds.height;
      const iw = video.videoWidth || 1920;
      const ih = video.videoHeight || 1080;
      const scale = Math.max(W / iw, H / ih);
      const dw = iw * scale;
      const dh = ih * scale;
      const dx = (W - dw) / 2;
      const dy = (H - dh) / 2;
      const ox = Math.round(window.screenX - display.bounds.x);
      const oy = Math.round(window.screenY - display.bounds.y);
      const sizeKey = `${Math.round(dw)}x${Math.round(dh)}`;
      if (sizeKey !== lastSize || force) {
        lastSize = sizeKey;
        video.style.width = `${Math.round(dw)}px`;
        video.style.height = `${Math.round(dh)}px`;
      }
      const moveKey = `${ox},${oy}`;
      if (moveKey === lastMove && !force) return;
      lastMove = moveKey;
      video.style.transform = `translate3d(${Math.round(dx - ox)}px, ${Math.round(dy - oy)}px, 0)`;
      return;
    }

    if (mode === 'capture' && video) {
      const W = display.bounds.width;
      const H = display.bounds.height;
      const ox = Math.round(window.screenX - display.bounds.x);
      const oy = Math.round(window.screenY - display.bounds.y);
      const sizeKey = `${W}x${H}`;
      if (sizeKey !== lastSize || force) {
        lastSize = sizeKey;
        video.style.width = `${Math.round(W)}px`;
        video.style.height = `${Math.round(H)}px`;
      }
      const moveKey = `${ox},${oy}`;
      if (moveKey === lastMove && !force) return;
      lastMove = moveKey;
      video.style.transform = `translate3d(${-ox}px, ${-oy}px, 0)`;
      return;
    }
    renderStatic(force);
  }

  /* ---------------- 性能：收起/隐藏时挂起 ---------------- */

  function shouldSuspend() {
    return document.hidden || document.body.classList.contains('mini');
  }

  function stopLoops() {
    if (raf) cancelAnimationFrame(raf);
    raf = null;
    if (timerDisplay) clearInterval(timerDisplay);
    timerDisplay = null;
    if (timerWall) clearInterval(timerWall);
    timerWall = null;
  }

  function startLoops() {
    stopLoops();
    timerDisplay = setInterval(() => refreshDisplay(false).then(() => render(true)), 2000);
    if (mode === 'static') timerWall = setInterval(refreshWallpaper, 3000);
    const loop = () => {
      if (!rolling) render(false);
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
  }

  function applySuspend() {
    const s = shouldSuspend();
    if (s === suspended) return;
    suspended = s;
    if (s) {
      stopLoops();
      // 只暂停，不销毁流：保留最后一帧，展开时不会白屏
      if (mode === 'capture') pauseCapture();
    } else {
      if (mode === 'capture') resumeCapture();
      startLoops();
    }
  }

  host.register({
    id: 'wallpaper',
    mount() {
      layer = document.getElementById('pluginLayer');
      if (!layer) return;
      layer.hidden = false;
      layer.style.background = 'transparent';
      document.body.classList.add('desktop-wallpaper');

      const o = opts();
      low = o.low;

      log('mount engine=' + o.engine + ' capture=' + o.capture);
      if (o.engine) {
        startEngine().catch((e) => { log('engine failed: ' + e); fallback('engine'); });
      } else if (o.capture) {
        startCapture().catch(() => fallback());
      } else {
        refreshWallpaper();
      }

      startLoops();
      refreshDisplay(true).then(() => render(true));

      obs = new MutationObserver(applySuspend);
      obs.observe(document.body, { attributes: true, attributeFilter: ['class'] });
      document.addEventListener('visibilitychange', applySuspend);
      window.addEventListener('resize', () => render(true));
      window.addEventListener('desknote:roll', onRoll);
      suspended = false;
      applySuspend();
    },
    unmount() {
      stopLoops();
      stopEngine();
      stopCapture(false);
      if (obs) obs.disconnect();
      obs = null;
      document.removeEventListener('visibilitychange', applySuspend);
      window.removeEventListener('desknote:roll', onRoll);
      document.body.classList.remove('desktop-wallpaper');
      if (layer) {
        layer.innerHTML = '';
        layer.style.backgroundImage = 'none';
        layer.hidden = true;
      }
      img = null;
      video = null;
      layer = null;
      display = null;
      natural = null;
      wallpaper = { path: '', style: 10, tile: false, mtime: 0 };
      lastMove = '';
      lastSize = '';
      mode = 'static';
      low = false;
      warned = false;
    },
  });
})();
