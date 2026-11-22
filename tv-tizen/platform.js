// Copyright (c) 2026 ESSVB. All Rights Reserved.
// Samsung Tizen (65" CU8500 Tizen 7.0, MU9000 Tizen 3.0 & Tizen 2.4+) HDMI TVWindow bridge (ES5).
(function () {
  'use strict';

  var KEYS = ['0', '1', '2', '3', '4', 'ColorF0Red', 'ColorF1Green', 'ColorF2Yellow', 'ColorF3Blue'];
  var FULL_RECT = ['0', '0', '100%', '100%'];
  var FRONT_RECT = ['12.5%', '0%', '75%', '75%'];

  function readTizenVersion() {
    try {
      var cap = String(tizen.systeminfo.getCapability('http://tizen.org/feature/platform.version') || '');
      if (cap) return cap;
    } catch (e) {}
    try {
      var m = /Tizen\s+(\d+\.\d+)/.exec(navigator.userAgent || '');
      return m ? m[1] : '';
    } catch (e) {
      return '';
    }
  }

  function isBelow24(ver) {
    var m = /^(\d+)\.(\d+)/.exec(String(ver || ''));
    if (!m) return false;
    var maj = Number(m[1]), min = Number(m[2]);
    return maj < 2 || (maj === 2 && min < 4);
  }

  var TIZEN_VER = readTizenVersion();
  var FRONT_ONLY = isBelow24(TIZEN_VER);
  var currentPort = 0;

  function parsePayloadValue(raw) {
    var s = String(raw || '');
    if (s.indexOf('b64:') === 0) {
      var bin = window.atob(s.slice(4));
      try {
        s = decodeURIComponent(escape(bin));
      } catch (e) {
        s = bin;
      }
    }
    var parsed = JSON.parse(s);
    return parsed && parsed.values !== undefined ? parsed.values : parsed;
  }

  function requestedParams() {
    try {
      var ctrl = tizen.application.getCurrentApplication().getRequestedAppControl().appControl;
      var data = (ctrl && ctrl.data) || [];
      for (var i = 0; i < data.length; i++) {
        if (data[i].key === 'PAYLOAD' && data[i].value && data[i].value.length) {
          return parsePayloadValue(data[i].value[0]);
        }
      }
    } catch (e) {}
    return null;
  }

  function hdmiSources(callback) {
    try {
      tizen.systeminfo.getPropertyValue('VIDEOSOURCE', function (info) {
        var connected = (info.connected || []).filter(function (s) { return s.type === 'HDMI'; });
        var disconnected = (info.disconnected || []).filter(function (s) { return s.type === 'HDMI'; });
        callback(connected.concat(disconnected));
      }, function () { callback([]); });
    } catch (e) {
      callback([]);
    }
  }

  function showTvWindow(onOk, onFail) {
    try {
      if (FRONT_ONLY) {
        tizen.tvwindow.show(onOk, onFail, FRONT_RECT, 'MAIN');
      } else {
        tizen.tvwindow.show(onOk, onFail, FULL_RECT, 'MAIN', 'BEHIND');
      }
    } catch (e) {
      onFail(e);
    }
  }

  window.ESSVB_PLATFORM = {
    backKey: 10009,
    tizenVersion: TIZEN_VER,
    subtitleBand: FRONT_ONLY ? 25 : 0,

    init: function () {
      for (var i = 0; i < KEYS.length; i++) {
        try { tizen.tvinputdevice.registerKey(KEYS[i]); } catch (e) {}
      }
      try {
        webapis.appcommon.setScreenSaver(webapis.appcommon.AppCommonScreenSaverState.SCREEN_SAVER_OFF);
      } catch (e) {}
    },

    launchParams: requestedParams,

    showHdmi: function (port, onStatus) {
      onStatus('loading');
      hdmiSources(function (all) {
        var matched = null;
        for (var i = 0; i < all.length; i++) {
          if (all[i].number === port) { matched = all[i]; break; }
        }
        if (!matched) {
          onStatus('sourceError');
          return;
        }
        try {
          tizen.tvwindow.setSource(matched, function () {
            currentPort = port;
            showTvWindow(function () {
              onStatus('playing');
            }, function () {
              onStatus('inputError');
            });
          }, function () {
            onStatus('inputError');
          }, 'MAIN');
        } catch (e) {
          onStatus('inputError');
        }
      });
    },

    exit: function () {
      try { tizen.tvwindow.hide(function () {}, function () {}, 'MAIN'); } catch (e) {}
      try { tizen.application.getCurrentApplication().exit(); } catch (e) {}
    }
  };
})();
