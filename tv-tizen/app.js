// Copyright (c) 2026 ESSVB. All Rights Reserved.
// ESSVB-sub Samsung Tizen TV Client (ES5, Chromium 47+ compatible for MU9000 & Tizen 2.4+).
(function () {
  'use strict';

  var platform = window.ESSVB_PLATFORM;
  var PORT = 8787;
  var KEY = { RED: 403, BACK: platform.backKey };

  var serverUrl = null;
  var eventSource = null;
  var hdmiPort = Number(load('hdmi')) || 1;
  var panelTimer = null;
  var hideTimer = null;
  var prevTimers = { 'subtitle-prev': [null, null], 'subtitle-prev2': [null, null] };
  var currentPlacement = 'fixed';
  var bottomPercent = 7;
  var stackPrevious = true;
  var activeText = '';
  var activeShownAt = 0;

  function $(id) { return document.getElementById(id); }

  function load(k) {
    try { return window.localStorage.getItem('essvb.' + k); } catch (e) { return null; }
  }

  function save(k, v) {
    try { window.localStorage.setItem('essvb.' + k, String(v)); } catch (e) {}
  }

  function getSavedToken(addr) {
    try {
      var map = JSON.parse(load('tokens') || '{}') || {};
      return map[addr] || '';
    } catch (e) {
      return '';
    }
  }

  function saveToken(addr, token) {
    try {
      var map = JSON.parse(load('tokens') || '{}') || {};
      map[addr] = token;
      save('tokens', JSON.stringify(map));
    } catch (e) {}
  }

  function setStatus(id, text, cls) {
    var el = $(id);
    if (!el) return;
    el.textContent = text;
    el.className = cls || '';
  }

  function togglePanel(show) {
    var p = $('panel');
    if (!p) return;
    clearTimeout(panelTimer);
    if (show === undefined) show = p.className === 'hidden';
    p.className = show ? '' : 'hidden';
    if (show) {
      panelTimer = setTimeout(function () { p.className = 'hidden'; }, 7000);
    }
  }

  function showHdmi(port) {
    hdmiPort = port;
    save('hdmi', port);
    setStatus('st-hdmi', 'HDMI ' + port);
    platform.showHdmi(port, function (st) {
      if (st === 'playing') setStatus('st-video', 'Aktif (' + (platform.tizenVersion || 'Tizen') + ')', 'ok');
      else if (st === 'loading') setStatus('st-video', 'Açılıyor…', 'muted');
      else setStatus('st-video', 'Sinyal yok / Seçilemedi', 'bad');
    });
  }

  function probeServer(url, onFound) {
    var xhr = new XMLHttpRequest();
    xhr.timeout = 1400;
    xhr.open('GET', url + '/hello', true);
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4 || xhr.status !== 200) return;
      try {
        var info = JSON.parse(xhr.responseText);
        if (info && info.app === 'essvb-sub') onFound(url, info);
      } catch (e) {}
    };
    try { xhr.send(); } catch (e) {}
  }

  function scanNetwork() {
    var saved = load('server');
    var found = false;
    var handleFound = function (url) {
      if (found) return;
      found = true;
      serverUrl = url;
      save('server', url);
      setStatus('st-server', url, 'ok');
      $('waiting').className = 'hidden';
      ensurePairedAndConnect(url);
    };

    if (saved) probeServer(saved, handleFound);

    var prefixes = ['192.168.1.', '192.168.0.', '10.0.0.'];
    try {
      var ip = tizen.systeminfo ? '' : '';
      if (window.webapis && webapis.network && webapis.network.getIp) {
        ip = webapis.network.getIp();
      }
      if (/^\d+\.\d+\.\d+\.\d+$/.test(ip) && ip !== '0.0.0.0') {
        prefixes = [ip.replace(/\.\d+$/, '.')];
      }
    } catch (e) {}

    for (var p = 0; p < prefixes.length; p++) {
      for (var i = 1; i <= 254; i++) {
        probeServer('http://' + prefixes[p] + i + ':' + PORT, handleFound);
      }
    }

    setTimeout(function () {
      if (!found) scanNetwork();
    }, 6000);
  }

  function ensurePairedAndConnect(url) {
    var token = getSavedToken(url);
    if (token) {
      openStream(url, token);
      return;
    }

    setStatus('st-conn', 'Eşleştirme PIN bekleniyor…', 'muted');
    var xhr = new XMLHttpRequest();
    xhr.open('POST', url + '/pair', true);
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4 || xhr.status !== 200) return;
      try {
        var ticket = JSON.parse(xhr.responseText);
        var box = $('pairing');
        box.textContent = 'ESSVB-sub Eşleştirme Kodu: ' + ticket.code + '  (Bilgisayardan onaylayın)';
        box.className = '';
        pollPairing(url, ticket.id);
      } catch (e) {}
    };
    xhr.send();
  }

  function pollPairing(url, ticketId) {
    var timer = setInterval(function () {
      var xhr = new XMLHttpRequest();
      xhr.open('GET', url + '/pair?id=' + encodeURIComponent(ticketId), true);
      xhr.onreadystatechange = function () {
        if (xhr.readyState !== 4 || xhr.status !== 200) return;
        try {
          var res = JSON.parse(xhr.responseText);
          if (res.state === 'approved' && res.token) {
            clearInterval(timer);
            $('pairing').className = 'hidden';
            saveToken(url, res.token);
            openStream(url, res.token);
          } else if (res.state === 'rejected' || res.state === 'expired') {
            clearInterval(timer);
            $('pairing').textContent = 'Eşleştirme süresi doldu veya reddedildi.';
          }
        } catch (e) {}
      };
      xhr.send();
    }, 1000);
  }

  function openStream(url, token) {
    if (eventSource) {
      try { eventSource.close(); } catch (e) {}
    }

    setStatus('st-conn', 'Bağlanıyor…', 'muted');
    eventSource = new EventSource(url + '/events?hdmi=' + hdmiPort + '&k=' + encodeURIComponent(token));

    eventSource.onopen = function () {
      setStatus('st-conn', 'Bağlı (Şifreli Oturum)', 'ok');
      $('waiting').className = 'hidden';
    };

    eventSource.onerror = function () {
      setStatus('st-conn', 'Yeniden bağlanılıyor…', 'bad');
    };

    eventSource.addEventListener('style', function (e) {
      try { applyStyle(JSON.parse(e.data)); } catch (err) {}
    });

    eventSource.addEventListener('subtitle', function (e) {
      try { showSubtitle(JSON.parse(e.data)); } catch (err) {}
    });

    eventSource.addEventListener('clear', function () {
      clearSubtitle();
    });

    eventSource.addEventListener('alert', function (e) {
      try {
        var payload = JSON.parse(e.data);
        var msg = (payload && payload.text) ? String(payload.text).trim() : '';
        var el = $('status-alert');
        if (!el) return;
        if (msg) {
          el.textContent = msg;
          el.className = '';
        } else {
          el.textContent = '';
          el.className = 'hidden';
        }
      } catch (err) {}
    });
  }

  function applyStyle(s) {
    if (!s) return;
    currentPlacement = s.placement || 'fixed';
    bottomPercent = typeof s.bottomPercent === 'number' ? s.bottomPercent : 7;
    stackPrevious = s.stackPrevious !== false;

    var el = $('subtitle-text');
    el.style.fontSize = (s.fontSize || 46) + 'px';
    el.style.fontWeight = s.bold ? '700' : '600';
    el.style.color = s.textColor === 'yellow' ? '#FFE135' : (s.textColor === 'cyan' ? '#38BDF8' : '#FFFFFF');

    var alpha = Math.max(0, Math.min(100, typeof s.background === 'number' ? s.background : 88)) / 100;
    el.style.background = alpha <= 0.02 ? 'transparent' : ('rgba(10, 13, 18, ' + alpha + ')');
    el.style.textShadow = alpha < 0.5
      ? '0 0 6px #000, 0 2px 6px #000, -1px -1px 0 #000, 1px 1px 0 #000'
      : '0 2px 4px rgba(0, 0, 0, 0.85)';

    var bodyCls = [];
    if (alpha < 0.5) bodyCls.push('thin-bg');
    if (s.backgroundShape === 'band') bodyCls.push('band');
    document.body.className = bodyCls.join(' ');
  }

  function clearSlot(id) {
    clearTimeout(prevTimers[id][0]);
    clearTimeout(prevTimers[id][1]);
    $(id).className = 'hidden';
  }

  function pushPrevious(text) {
    if (!stackPrevious || !activeText || activeText === text) {
      clearSlot('subtitle-prev');
      clearSlot('subtitle-prev2');
      return;
    }

    var p1 = $('subtitle-prev'), p1Text = $('subtitle-prev-text');
    var p2 = $('subtitle-prev2'), p2Text = $('subtitle-prev2-text');

    if (p1.className !== 'hidden' && p1Text.textContent) {
      p2Text.textContent = p1Text.textContent;
      p2.className = '';
      clearTimeout(prevTimers['subtitle-prev2'][0]);
      clearTimeout(prevTimers['subtitle-prev2'][1]);
      prevTimers['subtitle-prev2'][0] = setTimeout(function () { p2.className = 'fading'; }, 1500);
      prevTimers['subtitle-prev2'][1] = setTimeout(function () { p2.className = 'hidden'; }, 1900);
    }

    p1Text.textContent = activeText;
    p1.className = '';
    clearTimeout(prevTimers['subtitle-prev'][0]);
    clearTimeout(prevTimers['subtitle-prev'][1]);
    prevTimers['subtitle-prev'][0] = setTimeout(function () { p1.className = 'fading'; }, 1500);
    prevTimers['subtitle-prev'][1] = setTimeout(function () { p1.className = 'hidden'; }, 1900);
  }

  function paintSpeakers(el, text, speakers) {
    el.textContent = text;
    if (speakers && speakers.length) {
      var sp = speakers[0];
      if (text.indexOf(sp.name + ':') === 0) {
        el.textContent = '';
        var span = document.createElement('span');
        span.style.color = sp.color;
        span.textContent = sp.name + ': ';
        el.appendChild(span);
        el.appendChild(document.createTextNode(text.slice(sp.name.length + 1).replace(/^\s+/, '')));
        return;
      }
    }

    var parts = text.split('\n');
    if (parts.length > 1 && (/^[-—•]\s/.test(parts[0]) || /^[-—•]\s/.test(parts[1]))) {
      var palette = ['#F5B83D', '#38BDF8', '#34D399'];
      el.textContent = '';
      for (var i = 0; i < parts.length; i++) {
        if (i > 0) el.appendChild(document.createElement('br'));
        var lineSpan = document.createElement('span');
        lineSpan.style.color = palette[i % palette.length];
        lineSpan.textContent = parts[i];
        el.appendChild(lineSpan);
      }
    }
  }

  function showSubtitle(d) {
    var text = (d && d.text) || '';
    if (!text) return;

    var now = Date.now();
    if (!d.replace && (now - activeShownAt) < 1500) {
      pushPrevious(text);
    } else if (!d.replace) {
      clearSlot('subtitle-prev');
      clearSlot('subtitle-prev2');
    }

    activeText = text;
    activeShownAt = now;
    setStatus('st-last', text, '');

    var subEl = $('subtitle');
    var txtEl = $('subtitle-text');
    paintSpeakers(txtEl, text, d.speakers);

    var cover = $('subtitle-cover');
    if (d.box && currentPlacement === 'cover') {
      cover.style.left = (d.box.left * 100) + '%';
      cover.style.top = (d.box.top * 100) + '%';
      cover.style.width = ((d.box.right - d.box.left) * 100) + '%';
      cover.style.height = ((d.box.bottom - d.box.top) * 100) + '%';
      cover.className = '';
      subEl.style.bottom = Math.max(platform.subtitleBand, (1 - d.box.bottom) * 100) + '%';
    } else {
      cover.className = 'hidden';
      subEl.style.bottom = Math.max(platform.subtitleBand, bottomPercent) + '%';
    }

    subEl.className = '';
    clearTimeout(hideTimer);
    hideTimer = setTimeout(clearSubtitle, d.duration || 15000);
  }

  function clearSubtitle() {
    clearTimeout(hideTimer);
    $('subtitle').className = 'hidden';
    $('subtitle-cover').className = 'hidden';
    clearSlot('subtitle-prev');
    clearSlot('subtitle-prev2');
    activeText = '';
  }

  document.addEventListener('keydown', function (e) {
    var c = e.keyCode;
    if (c >= 49 && c <= 52) showHdmi(c - 48);
    else if (c === 48 || c === KEY.RED) togglePanel();
    else if (c === KEY.BACK) platform.exit();
  });

  platform.init();
  var launch = platform.launchParams ? platform.launchParams() : null;
  if (launch && launch.hdmi >= 1 && launch.hdmi <= 4) hdmiPort = Number(launch.hdmi);
  showHdmi(hdmiPort);
  togglePanel(true);
  $('waiting').className = '';

  if (launch && launch.server) {
    var addr = launch.server.replace(/\/+$/, '');
    if (launch.token) saveToken(addr, launch.token);
    serverUrl = addr;
    save('server', addr);
    setStatus('st-server', addr, 'ok');
    ensurePairedAndConnect(addr);
  } else {
    scanNetwork();
  }
})();
