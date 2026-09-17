/* ============================================================================
 * Rc 远程桌面 —— 移动端网页主控端（自包含，无框架、无构建、无外部资源）
 * ----------------------------------------------------------------------------
 * 线上格式以 Rc.Protocol 为准（Messages.cs / FrameCodec.cs）：
 *   每个 WebSocket 消息都是二进制帧：[1 字节消息类型][载荷]
 *   控制类载荷是 camelCase 的 UTF-8 JSON
 *   帧数据是小端序的 [帧头][瓦片…]，只包含发生变化的瓦片（增量）
 * 浏览器无法给 WebSocket 设置请求头，因此令牌走查询串：/control?id=…&token=…
 * ========================================================================== */
(function () {
  "use strict";

  /* ------------------------------------------------------------------ 协议常量 */

  var MSG = {
    HELLO: 1,
    HELLO_ACK: 2,
    FRAME: 3,
    INPUT: 4,
    CLIPBOARD: 5,
    CONTROL: 6,
    PING: 7,
    PONG: 8,
    ERROR: 9
  };

  var INPUT_KIND = {
    MOVE: "mouse_move",
    DOWN: "mouse_down",
    UP: "mouse_up",
    WHEEL: "wheel",
    KEY_DOWN: "key_down",
    KEY_UP: "key_up",
    TEXT: "text"
  };

  var CONTROL_KIND = {
    KEYFRAME: "request_keyframe",
    QUALITY: "set_quality",
    SCALE: "set_scale"
  };

  var BTN = { LEFT: 0, RIGHT: 1, MIDDLE: 2 };

  /* Windows 虚拟键码 */
  var VK = {
    BACKSPACE: 8, TAB: 9, ENTER: 13, ESC: 27,
    CTRL: 17, ALT: 18,
    LEFT: 37, UP: 38, RIGHT: 39, DOWN: 40,
    DELETE: 46, WIN: 91
  };

  var PROTOCOL_VERSION = 1;
  var DEFAULT_TILE_SIZE = 64;

  var FRAME_HEADER_BYTES = 21;  /* int32×4 + uint8 + int32 */
  var TILE_HEADER_BYTES = 20;   /* int32×5 */
  var MAX_TILES = 200000;
  var MAX_FRAME_PIXELS = 32768;

  var WS_OPEN = 1;
  var MAX_INFLIGHT_DECODES = 24;     /* 并行解码上限；不丢帧由队列保证，这里只管并发 */
  var STALL_MS = 1500;               /* 被控端在线却迟迟没有帧 → 主动要关键帧 */
  var KEYFRAME_MIN_GAP_MS = 1000;
  var RECONNECT_MIN_MS = 1000;
  var RECONNECT_MAX_MS = 15000;

  var MIN_ZOOM = 1;
  var MAX_ZOOM = 5;

  var TAP_SLOP_PX = 10;
  var LONG_PRESS_MS = 500;
  var WHEEL_STEP_PX = 40;            /* 每 40px 折合一个滚轮格 */
  var WHEEL_UNIT = 120;

  var FPS_WINDOW_MS = 1000;
  var ERROR_LINGER_MS = 6000;

  var STORAGE_KEY = "rc.web.client.v1";

  /* ------------------------------------------------------------------ DOM */

  function $(id) { return document.getElementById(id); }

  var connectScreen = $("connect-screen");
  var sessionScreen = $("session-screen");
  var connectForm = $("connect-form");
  var inputAgent = $("input-agent");
  var inputToken = $("input-token");
  var connectError = $("connect-error");

  var stage = $("stage");
  var canvas = $("screen");
  var waiting = $("waiting");
  var statusDot = $("status-dot");
  var statusState = $("status-state");
  var statusGeometry = $("status-geometry");
  var statusFps = $("status-fps");
  var sessionError = $("session-error");

  var toolbarToggle = $("toolbar-toggle");
  var toolbarBody = $("toolbar-body");
  var selQuality = $("sel-quality");
  var selScale = $("sel-scale");
  var btnKeyframe = $("btn-keyframe");
  var btnDrag = $("btn-drag");
  var btnDisconnect = $("btn-disconnect");
  var keysRow = $("keys");
  var inputText = $("input-text");
  var btnSend = $("btn-send");

  var ctx2d = canvas.getContext("2d");
  var utf8Encoder = new TextEncoder();
  var utf8Decoder = new TextDecoder();

  /* ------------------------------------------------------------------ 状态 */

  var view = { zoom: 1, tx: 0, ty: 0 };   /* 缩放/平移，单位是 CSS 像素 */

  var state = {
    screen: "connect",
    ws: null,
    gen: 0,
    stop: true,           /* true = 不再自动重连 */
    online: false,        /* socket 已打开 */
    peerOnline: false,    /* 被控端在线 */
    frameW: 0,
    frameH: 0,
    frameId: 0,
    gotFrame: false,
    lastFrameAt: 0,
    peerAt: 0,
    attempt: 0,
    reconnectTimer: 0,
    keyframeAt: 0,
    dragMode: false,
    /* 默认无损：桌面内容多为纯色块与文字，PNG 在这种内容上既没有压缩伪影，
       实测单帧还比 JPEG q95 更小。滚动场景若嫌卡，在工具栏下调一档即可。 */
    quality: 100,
    remoteScale: 100,
    fps: 0,
    frameTimes: [],
    errorTimer: 0,
    lastError: ""
  };

  /* 背缓冲：始终是完整的 frameWidth × frameHeight 远端画面 */
  var back = null;
  var backCtx = null;
  var backW = 0;
  var backH = 0;

  var dirty = true;
  var drawChain = Promise.resolve();
  var inflight = 0;

  /* ------------------------------------------------------------------ 小工具 */

  function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

  function now() {
    return (window.performance && window.performance.now)
      ? window.performance.now()
      : Date.now();
  }

  function errorText(err) {
    if (!err) { return "未知错误"; }
    if (typeof err === "string") { return err; }
    return err.message ? err.message : String(err);
  }

  function makeCanvas(w, h) {
    if (typeof OffscreenCanvas !== "undefined") {
      try { return new OffscreenCanvas(w, h); } catch (e) { /* 回退到普通 canvas */ }
    }
    var c = document.createElement("canvas");
    c.width = w;
    c.height = h;
    return c;
  }

  /* ------------------------------------------------------------------ 状态栏 */

  function setPill(stateText, dotClass) {
    statusState.textContent = stateText;
    statusDot.className = "status__dot" + (dotClass ? " " + dotClass : "");
  }

  function setGeometry(text) {
    statusGeometry.textContent = text;
  }

  function showSessionError(message) {
    state.lastError = message;
    sessionError.textContent = message;
    sessionError.hidden = false;
    if (state.errorTimer) { clearTimeout(state.errorTimer); }
    state.errorTimer = setTimeout(function () {
      state.errorTimer = 0;
      sessionError.hidden = true;
    }, ERROR_LINGER_MS);
  }

  function clearSessionError() {
    if (state.errorTimer) { clearTimeout(state.errorTimer); state.errorTimer = 0; }
    sessionError.hidden = true;
    state.lastError = "";
  }

  /* 任何异常都不允许逃出事件处理器：统一落到状态区。
     自身也不再向外抛，保证可以安全地放在 promise 链的兜底位置。 */
  function reportError(err) {
    try {
      var text = errorText(err);
      if (text === state.lastError) { return; }
      showSessionError("内部错误：" + text);
    } catch (e) { /* 最后一道防线 */ }
  }

  function updateWaitOverlay() {
    waiting.hidden = state.peerOnline;
    if (!state.peerOnline) {
      setPill(state.online ? "等待被控端" : "连接中", state.online ? "is-warn" : "");
    }
  }

  /* ------------------------------------------------------------------ 发送 */

  function sendBinary(type, bytes) {
    var ws = state.ws;
    if (!ws || ws.readyState !== WS_OPEN) { return false; }
    var size = 1 + (bytes ? bytes.length : 0);
    var out = new Uint8Array(size);
    out[0] = type;
    if (bytes && bytes.length) { out.set(bytes, 1); }
    try {
      ws.send(out.buffer);
      return true;
    } catch (e) {
      reportError(e);
      return false;
    }
  }

  function sendJson(type, obj) {
    var bytes;
    try {
      bytes = utf8Encoder.encode(JSON.stringify(obj));
    } catch (e) {
      reportError(e);
      return false;
    }
    return sendBinary(type, bytes);
  }

  function sendInput(obj) { return sendJson(MSG.INPUT, obj); }

  function sendControl(kind, value) {
    var msg = { kind: kind };
    if (typeof value === "number") { msg.value = value; }
    return sendJson(MSG.CONTROL, msg);
  }

  function requestKeyframe(force) {
    var t = now();
    if (!force && t - state.keyframeAt < KEYFRAME_MIN_GAP_MS) { return; }
    state.keyframeAt = t;
    sendControl(CONTROL_KIND.KEYFRAME, 0);
  }

  /* --------------------------------------------------------- 鼠标输入（归一化） */

  /* 把视口坐标换算成远端帧内的归一化坐标 0..1 */
  function normalizePoint(clientX, clientY) {
    if (!state.frameW || !state.frameH || !viewport.w || !viewport.h) { return null; }
    var scale = Math.min(viewport.w / state.frameW, viewport.h / state.frameH) * view.zoom;
    if (!(scale > 0)) { return null; }
    var drawW = state.frameW * scale;
    var drawH = state.frameH * scale;
    var drawX = viewport.w / 2 - drawW / 2 + view.tx;
    var drawY = viewport.h / 2 - drawH / 2 + view.ty;
    var fx = (clientX - drawX) / scale;
    var fy = (clientY - drawY) / scale;
    return {
      x: Math.round(clamp(fx / state.frameW, 0, 1) * 10000) / 10000,
      y: Math.round(clamp(fy / state.frameH, 0, 1) * 10000) / 10000
    };
  }

  /* 拖拽中的移动做逐帧合并，避免高频 pointermove 淹没输入队列 */
  var pendingMove = null;
  var moveRaf = 0;

  function queueMove(clientX, clientY) {
    var p = normalizePoint(clientX, clientY);
    if (!p) { return; }
    pendingMove = p;
    if (!moveRaf) {
      moveRaf = requestAnimationFrame(function () {
        moveRaf = 0;
        var m = pendingMove;
        pendingMove = null;
        if (m) { sendInput({ kind: INPUT_KIND.MOVE, x: m.x, y: m.y }); }
      });
    }
  }

  /* 关键顺序：任何按键/滚轮之前必须先落一帧 mouse_move，让远端光标先到位 */
  function flushMove() {
    if (moveRaf) { cancelAnimationFrame(moveRaf); moveRaf = 0; }
    var m = pendingMove;
    pendingMove = null;
    return m;
  }

  function sendAt(clientX, clientY, obj) {
    flushMove();                                    /* 丢弃排队中的旧坐标，重新取当前位置 */
    var p = normalizePoint(clientX, clientY);
    if (p) { sendInput({ kind: INPUT_KIND.MOVE, x: p.x, y: p.y }); }
    if (obj) { sendInput(obj); }
    return p;
  }

  function clickAt(clientX, clientY, button) {
    sendAt(clientX, clientY, { kind: INPUT_KIND.DOWN, button: button });
    sendInput({ kind: INPUT_KIND.UP, button: button });
  }

  /* 跟踪按下的左键：断线/切屏时补一个 up，避免被控端鼠标卡在按下状态 */
  var leftDown = false;

  function pressAt(clientX, clientY) {
    sendAt(clientX, clientY, { kind: INPUT_KIND.DOWN, button: BTN.LEFT });
    leftDown = true;
  }

  function releaseLeft(clientX, clientY) {
    if (leftDown) {
      if (typeof clientX === "number") {
        sendAt(clientX, clientY, null);
      }
      sendInput({ kind: INPUT_KIND.UP, button: BTN.LEFT });
      leftDown = false;
    }
  }

  function tapKey(vk, extended) {
    var ext = !!extended;
    sendInput({ kind: INPUT_KIND.KEY_DOWN, vk: vk, extended: ext });
    sendInput({ kind: INPUT_KIND.KEY_UP, vk: vk, extended: ext });
  }

  function sendCtrlAltDel() {
    sendInput({ kind: INPUT_KIND.KEY_DOWN, vk: VK.CTRL, extended: false });
    sendInput({ kind: INPUT_KIND.KEY_DOWN, vk: VK.ALT, extended: false });
    sendInput({ kind: INPUT_KIND.KEY_DOWN, vk: VK.DELETE, extended: true });
    sendInput({ kind: INPUT_KIND.KEY_UP, vk: VK.DELETE, extended: true });
    sendInput({ kind: INPUT_KIND.KEY_UP, vk: VK.ALT, extended: false });
    sendInput({ kind: INPUT_KIND.KEY_UP, vk: VK.CTRL, extended: false });
  }

  /* ------------------------------------------------------------------ 视图 */

  function baseScale() {
    if (!viewport.w || !viewport.h || !state.frameW || !state.frameH) { return 0; }
    return Math.min(viewport.w / state.frameW, viewport.h / state.frameH);
  }

  function clampView() {
    var scale = baseScale() * view.zoom;
    if (!scale) { return; }
    var mx = Math.max(0, (state.frameW * scale - viewport.w) / 2);
    var my = Math.max(0, (state.frameH * scale - viewport.h) / 2);
    view.tx = clamp(view.tx, -mx, mx);
    view.ty = clamp(view.ty, -my, my);
  }

  function resetView() {
    view.zoom = 1;
    view.tx = 0;
    view.ty = 0;
    dirty = true;
  }

  /* 以视口某点为锚点缩放：该点下的远端像素保持不动 */
  function zoomAround(clientX, clientY, nextZoom) {
    var target = clamp(nextZoom, MIN_ZOOM, MAX_ZOOM);
    if (Math.abs(target - view.zoom) < 0.0005) { return; }
    var base = baseScale();
    if (!base) { view.zoom = target; dirty = true; return; }

    var scaleBefore = base * view.zoom;
    var drawXBefore = viewport.w / 2 - (state.frameW * scaleBefore) / 2 + view.tx;
    var drawYBefore = viewport.h / 2 - (state.frameH * scaleBefore) / 2 + view.ty;
    var fx = (clientX - drawXBefore) / scaleBefore;
    var fy = (clientY - drawYBefore) / scaleBefore;

    view.zoom = target;

    var scaleAfter = base * view.zoom;
    var drawXAfter = viewport.w / 2 - (state.frameW * scaleAfter) / 2 + view.tx;
    var drawYAfter = viewport.h / 2 - (state.frameH * scaleAfter) / 2 + view.ty;
    view.tx += clientX - (drawXAfter + fx * scaleAfter);
    view.ty += clientY - (drawYAfter + fy * scaleAfter);

    clampView();
    dirty = true;
  }

  /* -------------------------------------------------------------- 背缓冲 */

  function ensureBack(width, height) {
    if (back && backW === width && backH === height) { return; }
    back = makeCanvas(width, height);
    backCtx = back.getContext("2d");
    backW = width;
    backH = height;
    if (backCtx) { backCtx.imageSmoothingEnabled = false; }
    dirty = true;
  }

  function decodeImage(blob) {
    if (typeof createImageBitmap === "function") {
      return createImageBitmap(blob);
    }
    /* 老版 iOS Safari 没有 createImageBitmap */
    return new Promise(function (resolve, reject) {
      var url = URL.createObjectURL(blob);
      var img = new Image();
      img.onload = function () { URL.revokeObjectURL(url); resolve(img); };
      img.onerror = function () { URL.revokeObjectURL(url); reject(new Error("图像解码失败")); };
      img.src = url;
    });
  }

  /* 被控端会按画质选择编码格式（画质=100 时用无损 PNG），而载荷里没有格式字段，
     因此只能嗅探魔数：89 50 4E 47 = PNG，FF D8 = JPEG。 */
  function imageMimeType(bytes) {
    if (bytes.length >= 4 && bytes[0] === 0x89 && bytes[1] === 0x50 &&
        bytes[2] === 0x4E && bytes[3] === 0x47) {
      return "image/png";
    }
    return "image/jpeg";
  }

  function drawTile(bitmap, px, py, w, h, frameW, frameH) {
    if (!backCtx || !bitmap) { return; }
    if (frameW !== backW || frameH !== backH) { return; }  /* 背缓冲已换代，丢弃过期瓦片 */
    if (px >= backW || py >= backH) { return; }
    var tw = w > 0 ? w : bitmap.width;
    var th = h > 0 ? h : bitmap.height;
    if (tw > backW - px) { tw = backW - px; }
    if (th > backH - py) { th = backH - py; }
    if (tw <= 0 || th <= 0) { return; }
    backCtx.drawImage(bitmap, px, py, tw, th);
  }

  /* Pending-tile queue.
     NEVER drop a tile here: frames are delta-encoded, so a dropped tile stays black until the next
     keyframe - and a keyframe arrives as one burst of every tile (510 of them for 1920x1080), which
     an "in-flight >= cap" check would reject synchronously after the first handful. That is exactly
     how the screen ends up almost entirely black. Queue instead, and cap only the DECODE
     concurrency. The queue is cleared only when it is genuinely swamped. */
  var pendingTiles = [];
  var MAX_PENDING_TILES = 8192;

  function enqueueTile(jpegBytes, px, py, w, h, frameW, frameH) {
    if (pendingTiles.length >= MAX_PENDING_TILES) {
      /* Genuinely swamped: drop the backlog and force a fresh full frame. */
      pendingTiles.length = 0;
      requestKeyframe(true);
      return;
    }
    pendingTiles.push({ jpeg: jpegBytes, px: px, py: py, w: w, h: h, frameW: frameW, frameH: frameH });
    pumpTiles();
  }

  function pumpTiles() {
    while (inflight < MAX_INFLIGHT_DECODES && pendingTiles.length > 0) {
      decodeTile(pendingTiles.shift());
    }
  }

  function decodeTile(tile) {
    inflight++;
    var blob = new Blob([tile.jpeg], { type: imageMimeType(tile.jpeg) });
    decodeImage(blob).then(function (bitmap) {
      /* 按到达顺序上屏；回调保证不抛异常，否则整条绘制链会永久中断 */
      drawChain = drawChain.then(function () {
        try {
          drawTile(bitmap, tile.px, tile.py, tile.w, tile.h, tile.frameW, tile.frameH);
        } catch (e) {
          reportError(e);
        } finally {
          try {
            if (bitmap && typeof bitmap.close === "function") { bitmap.close(); }
          } catch (e) { /* ignore */ }
          dirty = true;
        }
      }).catch(function (e) { reportError(e); });
      inflight--;
      pumpTiles();
    }, function () {
      /* 单块图像解码失败不值得打断会话：等下一次关键帧 */
      inflight--;
      pumpTiles();
    });
  }

  /* --------------------------------------------------------- 帧包解析（纯函数） */

  function parseFramePacket(bytes) {
    if (bytes.length < FRAME_HEADER_BYTES) {
      throw new Error("帧数据短于帧头");
    }
    var dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    var offset = 0;

    var frameWidth = dv.getInt32(offset, true); offset += 4;
    var frameHeight = dv.getInt32(offset, true); offset += 4;
    var tileSize = dv.getInt32(offset, true); offset += 4;
    var frameId = dv.getUint32(offset, true); offset += 4;
    var keyframe = bytes[offset] !== 0; offset += 1;
    var tileCount = dv.getInt32(offset, true); offset += 4;

    if (frameWidth <= 0 || frameHeight <= 0 ||
        frameWidth > MAX_FRAME_PIXELS || frameHeight > MAX_FRAME_PIXELS) {
      throw new Error("非法的帧尺寸 " + frameWidth + "x" + frameHeight);
    }
    if (tileSize <= 0 || tileSize > MAX_FRAME_PIXELS) {
      throw new Error("非法的瓦片尺寸 " + tileSize);
    }
    if (tileCount < 0 || tileCount > MAX_TILES) {
      throw new Error("非法的瓦片数量 " + tileCount);
    }

    var tiles = [];
    for (var i = 0; i < tileCount; i++) {
      if (offset + TILE_HEADER_BYTES > bytes.length) { break; }
      var gridX = dv.getInt32(offset, true); offset += 4;
      var gridY = dv.getInt32(offset, true); offset += 4;
      var width = dv.getInt32(offset, true); offset += 4;
      var height = dv.getInt32(offset, true); offset += 4;
      var jpegLength = dv.getInt32(offset, true); offset += 4;
      if (jpegLength < 0 || offset + jpegLength > bytes.length) { break; }
      tiles.push({
        gridX: gridX,
        gridY: gridY,
        width: width,
        height: height,
        jpeg: bytes.subarray(offset, offset + jpegLength)
      });
      offset += jpegLength;
    }

    return {
      frameWidth: frameWidth,
      frameHeight: frameHeight,
      tileSize: tileSize,
      frameId: frameId,
      keyframe: keyframe,
      tiles: tiles
    };
  }

  function handleFramePacket(bytes) {
    var packet = parseFramePacket(bytes);

    if (packet.frameWidth !== backW || packet.frameHeight !== backH) {
      /* 主控端可以改采集缩放，画面尺寸会中途变化：整块背缓冲换代，旧瓦片自然作废 */
      ensureBack(packet.frameWidth, packet.frameHeight);
      state.frameW = packet.frameWidth;
      state.frameH = packet.frameHeight;
      clampView();
      setGeometry(packet.frameWidth + "×" + packet.frameHeight);
    }

    state.gotFrame = true;

    for (var i = 0; i < packet.tiles.length; i++) {
      var t = packet.tiles[i];
      enqueueTile(t.jpeg, t.gridX * packet.tileSize, t.gridY * packet.tileSize,
        t.width, t.height, packet.frameWidth, packet.frameHeight);
    }

    state.frameId = packet.frameId;
    state.lastFrameAt = now();
    state.frameTimes.push(state.lastFrameAt);
    if (packet.keyframe) { clearSessionError(); }
    dirty = true;
  }

  /* ------------------------------------------------------------------ 渲染 */

  /* 视口快照：只在窗口尺寸变化时重新测量，避免每帧强制布局计算 */
  var viewport = { w: 0, h: 0, dpr: 1 };
  var needMeasure = true;

  function measureViewport() {
    var dpr = clamp(window.devicePixelRatio || 1, 1, 3);
    var cssW = stage.clientWidth || window.innerWidth;
    var cssH = stage.clientHeight || window.innerHeight;
    var bw = Math.max(1, Math.round(cssW * dpr));
    var bh = Math.max(1, Math.round(cssH * dpr));
    if (canvas.width !== bw || canvas.height !== bh) {
      canvas.width = bw;
      canvas.height = bh;
      dirty = true;
    }
    viewport.w = cssW;
    viewport.h = cssH;
    viewport.dpr = dpr;
  }

  function draw() {
    if (!ctx2d) { return; }
    var w = viewport.w, h = viewport.h;
    ctx2d.setTransform(viewport.dpr, 0, 0, viewport.dpr, 0, 0);
    ctx2d.clearRect(0, 0, w, h);
    if (!back || !backW || !backH || !w || !h) { return; }

    var scale = Math.min(w / backW, h / backH) * view.zoom;
    var drawW = backW * scale;
    var drawH = backH * scale;
    var drawX = w / 2 - drawW / 2 + view.tx;
    var drawY = h / 2 - drawH / 2 + view.ty;

    /* 放大到 1x 以上时关掉插值，像素保持锐利 */
    ctx2d.imageSmoothingEnabled = view.zoom <= 1;
    ctx2d.drawImage(back, drawX, drawY, drawW, drawH);
  }

  function tickFps(t) {
    var times = state.frameTimes;
    while (times.length && t - times[0] > FPS_WINDOW_MS) { times.shift(); }
    if (times.length !== state.fps) {
      state.fps = times.length;
      statusFps.textContent = state.fps + " fps";
    }
  }

  function frameLoop() {
    requestAnimationFrame(frameLoop);
    try {
      tickFps(now());
      if (needMeasure) {
        needMeasure = false;
        measureViewport();
      }
      if (dirty) {
        dirty = false;
        draw();
      }
    } catch (e) {
      reportError(e);
    }
  }

  /* ------------------------------------------------------------------ 手势 */

  var pointers = new Map();     /* pointerId -> { x, y } */
  var gesture = null;
  var longPressTimer = 0;

  function clearLongPress() {
    if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = 0; }
  }

  function pointerList() {
    var out = [];
    pointers.forEach(function (p) { out.push(p); });
    return out;
  }

  function beginSingle(ev) {
    gesture = {
      kind: "single",
      id: ev.pointerId,
      sx: ev.clientX,
      sy: ev.clientY,
      lx: ev.clientX,
      ly: ev.clientY,
      t0: now(),
      moved: false,
      pressed: false,
      longPress: false
    };
    if (state.dragMode) {
      /* 拖拽模式：按下即落左键，之后的移动直接带动远端 */
      pressAt(ev.clientX, ev.clientY);
      gesture.pressed = true;
    } else {
      clearLongPress();
      longPressTimer = setTimeout(function () {
        longPressTimer = 0;
        try {
          if (state.screen !== "session" || !gesture || gesture.kind !== "single") { return; }
          if (gesture.moved || gesture.pressed) { return; }
          gesture.longPress = true;
          clickAt(gesture.sx, gesture.sy, BTN.RIGHT);
        } catch (e) {
          reportError(e);
        }
      }, LONG_PRESS_MS);
    }
  }

  function beginMulti() {
    clearLongPress();
    /* 单指拖拽途中加入第二根手指：先松开左键，避免远端卡在按下状态 */
    if (gesture && gesture.kind === "single" && gesture.pressed) {
      releaseLeft(gesture.lx, gesture.ly);
    }
    var pts = pointerList();
    var a = pts[0], b = pts[1];
    gesture = {
      kind: "multi",
      lastDist: Math.hypot(a.x - b.x, a.y - b.y),
      lastMidX: (a.x + b.x) / 2,
      lastMidY: (a.y + b.y) / 2,
      wheelAcc: 0,
      pinched: false
    };
  }

  function onPointerDown(ev) {
    try {
      ev.preventDefault();
      try { canvas.setPointerCapture(ev.pointerId); } catch (e) { /* 可选 */ }
      pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
      if (pointers.size === 1) {
        beginSingle(ev);
      } else if (pointers.size === 2) {
        beginMulti();
      }
    } catch (e) {
      reportError(e);
    }
  }

  function onPointerMove(ev) {
    try {
      if (!pointers.has(ev.pointerId)) { return; }
      pointers.set(ev.pointerId, { x: ev.clientX, y: ev.clientY });
      if (!gesture) { return; }

      if (gesture.kind === "single" && pointers.size === 1) {
        var dx = ev.clientX - gesture.lx;
        var dy = ev.clientY - gesture.ly;
        gesture.lx = ev.clientX;
        gesture.ly = ev.clientY;

        if (!gesture.moved) {
          var far = Math.hypot(ev.clientX - gesture.sx, ev.clientY - gesture.sy);
          if (far > TAP_SLOP_PX) {
            gesture.moved = true;
            clearLongPress();
          }
        }

        if (gesture.pressed) {
          queueMove(ev.clientX, ev.clientY);
        } else if (gesture.moved && view.zoom > 1.001) {
          view.tx += dx;
          view.ty += dy;
          clampView();
          dirty = true;
        } else if (gesture.moved && !gesture.longPress) {
          /* 未缩放时单指平移不生效，按规格忽略 */
        }
        return;
      }

      if (pointers.size >= 2) {
        if (gesture.kind !== "multi") { beginMulti(); }
        if (!gesture || gesture.kind !== "multi") { return; }

        var pts = pointerList();
        var a = pts[0], b = pts[1];
        var dist = Math.hypot(a.x - b.x, a.y - b.y);
        var midX = (a.x + b.x) / 2;
        var midY = (a.y + b.y) / 2;

        /* 双指捏合 → 以捏合中心缩放 */
        if (gesture.lastDist > 8 && dist > 8 && Math.abs(dist - gesture.lastDist) > 0.5) {
          zoomAround(midX, midY, view.zoom * (dist / gesture.lastDist));
          gesture.pinched = true;
        }
        gesture.lastDist = dist;

        /* 双指纵向拖动 → 滚轮，约 40px 一格（120） */
        var moveY = midY - gesture.lastMidY;
        gesture.lastMidX = midX;
        gesture.lastMidY = midY;
        if (Math.abs(moveY) < 80) {
          gesture.wheelAcc += moveY;
          var steps = Math.trunc(gesture.wheelAcc / WHEEL_STEP_PX);
          if (steps !== 0) {
            gesture.wheelAcc -= steps * WHEEL_STEP_PX;
            steps = clamp(steps, -3, 3);
            sendAt(midX, midY, { kind: INPUT_KIND.WHEEL, delta: steps * WHEEL_UNIT });
          }
        }
        return;
      }
    } catch (e) {
      reportError(e);
    }
  }

  function onPointerUp(ev, cancelled) {
    try {
      if (!pointers.has(ev.pointerId)) { return; }
      pointers.delete(ev.pointerId);

      if (gesture && gesture.kind === "single" && gesture.id === ev.pointerId) {
        clearLongPress();
        var g = gesture;
        if (g.pressed) {
          releaseLeft(ev.clientX, ev.clientY);
        } else if (!cancelled && !g.longPress && !g.moved &&
                   now() - g.t0 < LONG_PRESS_MS) {
          /* 轻触 = 左键单击 */
          clickAt(ev.clientX, ev.clientY, BTN.LEFT);
        }
      }

      if (pointers.size === 0) {
        clearLongPress();
        gesture = null;
      } else if (pointers.size === 1) {
        /* 双指抬起一根：剩下的那根继续当平移用，不再触发轻触 */
        var rest = pointerList()[0];
        gesture = {
          kind: "single",
          id: 0,
          sx: rest.x,
          sy: rest.y,
          lx: rest.x,
          ly: rest.y,
          t0: now(),
          moved: true,
          pressed: false,
          longPress: true
        };
      }
    } catch (e) {
      reportError(e);
    }
  }

  /* 桌面浏览器：真滚轮直接转发 */
  function onWheel(ev) {
    try {
      ev.preventDefault();
      var steps = ev.deltaY > 0 ? -1 : 1;
      if (Math.abs(ev.deltaY) > 40) { steps = ev.deltaY > 0 ? -3 : 3; }
      sendAt(ev.clientX, ev.clientY, { kind: INPUT_KIND.WHEEL, delta: steps * WHEEL_UNIT });
    } catch (e) {
      reportError(e);
    }
  }

  /* ------------------------------------------------------------------ 收发 */

  function handleHelloAck(msg) {
    if (!msg || typeof msg !== "object") { return; }

    if (msg.ok === false) {
      fatal(msg.error ? String(msg.error) : "中转服务拒绝了本次连接");
      return;
    }

    state.peerOnline = !!msg.peerOnline;
    if (state.peerOnline) { state.peerAt = now(); }

    /* 还没有帧时先用 HelloAck 里的几何信息占位；最终尺寸以帧包为准 */
    if (msg.screenWidth > 0 && msg.screenHeight > 0 && !state.gotFrame) {
      setGeometry(msg.screenWidth + "×" + msg.screenHeight);
    }

    state.attempt = 0;
    updateWaitOverlay();

    /* 被控端可能先于/后于主控端上线，中转会在它上线时再推一次 HelloAck：
       每次握手成功都主动补一帧完整画面。 */
    if (state.peerOnline) {
      setPill("已连接", "is-ok");
      /* 让工具栏里显示的画面参数成为实际生效的参数 */
      sendControl(CONTROL_KIND.QUALITY, state.quality);
      sendControl(CONTROL_KIND.SCALE, state.remoteScale);
      if (!state.gotFrame || now() - state.lastFrameAt > 1000) {
        requestKeyframe(true);
      }
    }
  }

  function handleServerError(msg) {
    var text = msg && msg.message ? String(msg.message) : "中转服务返回错误";
    showSessionError(text);
  }

  function decodeJson(bytes) {
    try {
      return JSON.parse(utf8Decoder.decode(bytes));
    } catch (e) {
      return null;
    }
  }

  function handleMessage(data) {
    if (typeof Blob !== "undefined" && data instanceof Blob) {
      /* binaryType 未及时生效时的兜底 */
      data.arrayBuffer().then(function (buf) { handleMessage(buf); }, reportError);
      return;
    }
    if (!(data instanceof ArrayBuffer)) { return; }
    var bytes = new Uint8Array(data);
    if (bytes.length < 1) { return; }

    var type = bytes[0];
    var payload = bytes.subarray(1);

    if (type === MSG.PING) {
      sendBinary(MSG.PONG, null);
      return;
    }
    if (type === MSG.FRAME) {
      handleFramePacket(payload);
      return;
    }
    if (type === MSG.HELLO_ACK) {
      handleHelloAck(decodeJson(payload));
      return;
    }
    if (type === MSG.ERROR) {
      handleServerError(decodeJson(payload));
      return;
    }
    /* 其余类型（Clipboard 等）本客户端不处理 */
  }

  /* ------------------------------------------------------------ 连接生命周期 */

  function controlUrl(agentId, token) {
    var scheme = location.protocol === "https:" ? "wss:" : "ws:";
    return scheme + "//" + location.host + "/control" +
      "?id=" + encodeURIComponent(agentId) +
      "&token=" + encodeURIComponent(token);
  }

  function closeSocket() {
    var ws = state.ws;
    try { releaseLeft(); } catch (e) { /* ignore */ }
    state.ws = null;
    state.online = false;
    if (!ws) { return; }
    try {
      ws.onopen = null;
      ws.onmessage = null;
      ws.onerror = null;
      ws.onclose = null;
    } catch (e) { /* ignore */ }
    try { ws.close(1000, "client closing"); } catch (e) { /* ignore */ }
  }

  function scheduleReconnect() {
    if (state.stop || state.screen !== "session") { return; }
    var delay = Math.min(RECONNECT_MAX_MS, RECONNECT_MIN_MS * Math.pow(2, state.attempt));
    state.attempt++;
    var seconds = Math.round(delay / 1000);
    setPill("重连中 " + seconds + "s", "is-warn");
    if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); }
    state.reconnectTimer = setTimeout(function () {
      state.reconnectTimer = 0;
      connect();
    }, delay);
  }

  function connect() {
    if (state.stop || state.screen !== "session") { return; }
    var agentId = inputAgent.value.trim();
    var token = inputToken.value;
    if (!agentId || !token) { return; }

    closeSocket();

    var ws;
    try {
      ws = new WebSocket(controlUrl(agentId, token));
    } catch (e) {
      showSessionError("无法建立 WebSocket：" + errorText(e));
      scheduleReconnect();
      return;
    }

    ws.binaryType = "arraybuffer";
    state.ws = ws;
    state.online = false;
    var gen = ++state.gen;

    setPill("连接中", "");
    updateWaitOverlay();

    ws.onopen = function () {
      try {
        if (gen !== state.gen) { return; }
        state.online = true;
        sendJson(MSG.HELLO, {
          role: "control",
          version: PROTOCOL_VERSION,
          screenWidth: 0,
          screenHeight: 0,
          tileSize: DEFAULT_TILE_SIZE
        });
        setPill("握手中", "is-warn");
        updateWaitOverlay();
      } catch (e) {
        reportError(e);
      }
    };

    ws.onmessage = function (ev) {
      try {
        if (gen !== state.gen) { return; }
        handleMessage(ev.data);
      } catch (e) {
        reportError(e);
      }
    };

    ws.onerror = function () {
      try {
        if (gen !== state.gen) { return; }
        setPill("连接错误", "is-err");
      } catch (e) {
        reportError(e);
      }
    };

    ws.onclose = function () {
      try {
        if (gen !== state.gen) { return; }
        state.online = false;
        state.peerOnline = false;
        state.ws = null;
        leftDown = false;
        updateWaitOverlay();
        if (state.stop) {
          setPill("已断开", "is-err");
          return;
        }
        setPill("连接断开", "is-err");
        scheduleReconnect();
      } catch (e) {
        reportError(e);
      }
    };
  }

  function fatal(message) {
    state.stop = true;
    closeSocket();
    if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); state.reconnectTimer = 0; }
    showConnectScreen(message);
  }

  /* ------------------------------------------------------------------ 看门狗 */

  function stallWatchdog() {
    try {
      if (state.screen !== "session" || !state.online || !state.peerOnline) { return; }
      var since = Math.max(state.lastFrameAt, state.peerAt);
      if (since && now() - since > STALL_MS) { requestKeyframe(false); }
    } catch (e) {
      reportError(e);
    }
  }

  /* ------------------------------------------------------------------ 界面切换 */

  function showConnectScreen(message) {
    state.screen = "connect";
    state.stop = true;
    state.fps = 0;
    state.frameTimes = [];
    closeSocket();
    if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); state.reconnectTimer = 0; }
    sessionScreen.hidden = true;
    connectScreen.hidden = false;
    clearLongPress();
    pointers.clear();
    gesture = null;

    if (message) {
      connectError.textContent = message;
      connectError.hidden = false;
    } else {
      connectError.textContent = "";
      connectError.hidden = true;
    }
  }

  function showSessionScreen() {
    state.screen = "session";
    state.stop = false;
    state.attempt = 0;
    state.gotFrame = false;
    state.lastFrameAt = 0;
    state.peerAt = 0;
    state.frameW = 0;
    state.frameH = 0;
    back = null;
    backCtx = null;
    backW = 0;
    backH = 0;
    state.frameTimes = [];
    state.fps = -1;
    resetView();
    connectScreen.hidden = true;
    sessionScreen.hidden = false;
    clearSessionError();
    needMeasure = true;
    setGeometry("--");
    setPill("连接中", "");
    waiting.hidden = true;
    syncKeyboardInset();
    connect();
  }

  function leaveSession() {
    state.stop = true;
    closeSocket();
    if (state.reconnectTimer) { clearTimeout(state.reconnectTimer); state.reconnectTimer = 0; }
    showConnectScreen("");
  }

  /* ------------------------------------------------------- 软键盘 / 视口适配 */

  function syncKeyboardInset() {
    var vv = window.visualViewport;
    var inset = 0;
    if (vv) {
      inset = Math.max(0, (window.innerHeight - vv.height - vv.offsetTop));
    }
    document.documentElement.style.setProperty("--kb-inset", inset.toFixed(1) + "px");
    dirty = true;
  }

  /* ------------------------------------------------------------------ 文本输入 */

  var composing = false;

  function flushText() {
    var value = inputText.value;
    if (!value) { return; }
    /* 只有真正发出去了才清空，断线期间不丢用户已输入的文字 */
    if (sendInput({ kind: INPUT_KIND.TEXT, text: value })) {
      inputText.value = "";
    }
  }

  /* ---------------------------------------------------------------- 偏好存储 */

  function savePrefs() {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify({
        id: inputAgent.value.trim(),
        token: inputToken.value,
        quality: selQuality.value,
        scale: selScale.value,
        drag: state.dragMode
      }));
    } catch (e) { /* 隐私模式/配额：忽略即可 */ }
  }

  function loadPrefs() {
    var raw = null;
    try { raw = window.localStorage.getItem(STORAGE_KEY); } catch (e) { return; }
    if (!raw) { return; }
    var prefs;
    try { prefs = JSON.parse(raw); } catch (e) { return; }
    if (!prefs || typeof prefs !== "object") { return; }
    if (typeof prefs.id === "string") { inputAgent.value = prefs.id; }
    if (typeof prefs.token === "string") { inputToken.value = prefs.token; }
    if (["100", "95", "75", "55", "40"].indexOf(prefs.quality) >= 0) { selQuality.value = prefs.quality; }
    if (["100", "75", "50"].indexOf(prefs.scale) >= 0) { selScale.value = prefs.scale; }
    setDragMode(!!prefs.drag);
    state.quality = parseInt(selQuality.value, 10) || 75;
    state.remoteScale = parseInt(selScale.value, 10) || 100;
  }

  function setDragMode(on) {
    state.dragMode = !!on;
    btnDrag.setAttribute("aria-pressed", state.dragMode ? "true" : "false");
  }

  /* ------------------------------------------------------------------ 绑定 */

  function bind() {
    connectForm.addEventListener("submit", function (ev) {
      try {
        ev.preventDefault();
        var agentId = inputAgent.value.trim();
        var token = inputToken.value;
        if (!agentId) {
          connectError.textContent = "请填写被控端 ID。";
          connectError.hidden = false;
          inputAgent.focus();
          return;
        }
        if (!token) {
          connectError.textContent = "请填写令牌。";
          connectError.hidden = false;
          inputToken.focus();
          return;
        }
        connectError.hidden = true;
        savePrefs();
        try { inputAgent.blur(); inputToken.blur(); } catch (e) { /* ignore */ }
        showSessionScreen();
      } catch (e) {
        connectError.textContent = "内部错误：" + errorText(e);
        connectError.hidden = false;
      }
    });

    toolbarToggle.addEventListener("click", function () {
      try {
        var open = toolbarToggle.getAttribute("aria-expanded") !== "false";
        toolbarToggle.setAttribute("aria-expanded", open ? "false" : "true");
        toolbarBody.hidden = open;
      } catch (e) {
        reportError(e);
      }
    });

    selQuality.addEventListener("change", function () {
      try {
        state.quality = parseInt(selQuality.value, 10) || 75;
        sendControl(CONTROL_KIND.QUALITY, state.quality);
        savePrefs();
      } catch (e) {
        reportError(e);
      }
    });

    selScale.addEventListener("change", function () {
      try {
        state.remoteScale = parseInt(selScale.value, 10) || 100;
        sendControl(CONTROL_KIND.SCALE, state.remoteScale);
        savePrefs();
      } catch (e) {
        reportError(e);
      }
    });

    btnKeyframe.addEventListener("click", function () {
      try { requestKeyframe(true); } catch (e) { reportError(e); }
    });

    btnDrag.addEventListener("click", function () {
      try {
        setDragMode(!state.dragMode);
        savePrefs();
      } catch (e) {
        reportError(e);
      }
    });

    btnDisconnect.addEventListener("click", function () {
      try { leaveSession(); } catch (e) { reportError(e); }
    });

    keysRow.addEventListener("click", function (ev) {
      try {
        var btn = ev.target && ev.target.closest ? ev.target.closest("button[data-key]") : null;
        if (!btn || btn.disabled) { return; }
        if (btn.getAttribute("data-combo") === "cad") {
          sendCtrlAltDel();
          return;
        }
        var vk = parseInt(btn.getAttribute("data-vk"), 10);
        if (isNaN(vk)) { return; }
        tapKey(vk, btn.getAttribute("data-ext") === "1");
      } catch (e) {
        reportError(e);
      }
    });

    btnSend.addEventListener("click", function () {
      try { flushText(); } catch (e) { reportError(e); }
    });

    inputText.addEventListener("compositionstart", function () {
      composing = true;
    });

    inputText.addEventListener("compositionend", function () {
      composing = false;
      /* 部分浏览器先派发 compositionend 再派发 input，延后一拍收尾 */
      setTimeout(function () {
        try { flushText(); } catch (e) { reportError(e); }
      }, 0);
    });

    /* 中文输入法：把输入框里已提交的文字整段发出去（绝不从 keydown 合成 IME 结果） */
    inputText.addEventListener("input", function () {
      try {
        if (composing) { return; }
        flushText();
      } catch (e) {
        reportError(e);
      }
    });

    inputText.addEventListener("keydown", function (ev) {
      try {
        if (ev.key !== "Enter" || ev.isComposing || composing) { return; }
        ev.preventDefault();
        flushText();
      } catch (e) {
        reportError(e);
      }
    });

    canvas.addEventListener("pointerdown", onPointerDown, { passive: false });
    canvas.addEventListener("pointermove", onPointerMove, { passive: false });
    canvas.addEventListener("pointerup", function (ev) { onPointerUp(ev, false); });
    canvas.addEventListener("pointercancel", function (ev) { onPointerUp(ev, true); });
    canvas.addEventListener("wheel", onWheel, { passive: false });
    canvas.addEventListener("contextmenu", function (ev) { ev.preventDefault(); });

    /* 会话页禁止整页缩放/双击缩放，避免与画布手势打架 */
    sessionScreen.addEventListener("gesturestart", function (ev) { ev.preventDefault(); });
    sessionScreen.addEventListener("gesturechange", function (ev) { ev.preventDefault(); });

    window.addEventListener("resize", function () {
      try {
        needMeasure = true;
        syncKeyboardInset();
        clampView();
        dirty = true;
      } catch (e) {
        reportError(e);
      }
    });

    if (window.visualViewport) {
      window.visualViewport.addEventListener("resize", function () {
        try {
          needMeasure = true;
          syncKeyboardInset();
        } catch (e) {
          reportError(e);
        }
      });
      window.visualViewport.addEventListener("scroll", function () {
        try { syncKeyboardInset(); } catch (e) { reportError(e); }
      });
    }

    window.addEventListener("orientationchange", function () {
      try {
        resetView();
        needMeasure = true;
        syncKeyboardInset();
      } catch (e) {
        reportError(e);
      }
    });

    window.addEventListener("error", function (ev) {
      if (state.screen === "session") { reportError(ev.message || "脚本错误"); }
    });

    window.addEventListener("unhandledrejection", function (ev) {
      if (state.screen === "session") { reportError(ev.reason || "未处理的异步错误"); }
    });

    document.addEventListener("visibilitychange", function () {
      /* 回到前台时画面多半已经过期，主动补一帧 */
      if (document.visibilityState === "visible" && state.screen === "session" && state.online) {
        requestKeyframe(true);
      }
    });
  }

  /* ------------------------------------------------------------------ 启动 */

  function boot() {
    loadPrefs();
    bind();
    setDragMode(state.dragMode);
    showConnectScreen("");
    setPill("未连接", "");
    setInterval(stallWatchdog, 1000);
    requestAnimationFrame(frameLoop);
  }

  boot();
})();
