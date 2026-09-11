/*
 * 示例启动器插件脚本：在页面里暴露桌面端上下文，并注册一个 Alt+Shift+D 诊断快捷键。
 * 注入脚本在每个文档创建时执行，因此只做幂等操作。
 */
(function () {
  if (window.__DSH_DESKTOP_BRAND__) {
    return;
  }
  window.__DSH_DESKTOP_BRAND__ = {
    plugin: 'example-branding',
    injectedAt: new Date().toISOString(),
    desktop: window.__DSH_DESKTOP__ || null
  };

  document.addEventListener('keydown', function (event) {
    if (event.altKey && event.shiftKey && (event.key === 'D' || event.key === 'd')) {
      var info = window.__DSH_DESKTOP__ || {};
      var lines = [
        'DSH Desktop ' + (info.version || '?'),
        '安装目录: ' + (info.root || '?'),
        '插件: ' + ((info.plugins || []).join(', ') || '(无)'),
        'LAN: ' + (info.lan ? 'on' : 'off'),
        '地址: ' + location.href
      ];
      window.alert(lines.join('\n'));
    }
  });
})();
