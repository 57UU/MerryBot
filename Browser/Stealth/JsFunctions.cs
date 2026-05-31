namespace BrowserService.Stealth;

/// <summary>
/// 用于浏览器隐身保护的 JavaScript 函数代码
/// 这些脚本通过 Page.addScriptToEvaluateOnNewDocument 注入到每个新页面中
/// </summary>
internal static class JsFunctions
{
    /// <summary>
    /// 核心工具包 - 提供基础工具函数
    /// </summary>
    public const string SeleniumStealth_RequiredUtilityPack = @"
(function(){
    window.__defineGetter__ = window.__defineGetter__ || {};
    window.__lookupGetter__ = window.__lookupGetter__ || {};
    if (typeof window.__proto__ === 'undefined') {
        Object.defineProperty(Object.prototype, '__proto__', {
            get: function() { return Object.getPrototypeOf(this); },
            set: function(o) { Object.setPrototypeOf(this, o); }
        });
    }
})();";

    /// <summary>
    /// 伪装 Chrome App 存在
    /// </summary>
    public const string SeleniumStealth_FakeChromeApp = @"
(function(){
    if (!window.chrome) {
        window.chrome = {};
    }
    if (!window.chrome.runtime) {
        window.chrome.runtime = {};
    }
    const runtimed = {
        id: 'abc123',
        lastError: undefined,
        connect: function(){},
        sendMessage: function(){},
        getManifest: function(){ return {version: '1.0', name: 'Chrome'}; },
        getURL: function(path){ return 'chrome-extension://abc123/' + path; },
        onInstalled: { addListener: function(){}, removeListener: function(){} },
        onMessage: { addListener: function(){}, removeListener: function(){} },
        onStartup: { addListener: function(){}, removeListener: function(){} },
        onSuspend: { addListener: function(){}, removeListener: function(){} },
        onSuspendCanceled: { addListener: function(){}, removeListener: function(){} },
        onUpdateAvailable: { addListener: function(){}, removeListener: function(){} },
        onConnect: { addListener: function(){}, removeListener: function(){} },
        onConnectExternal: { addListener: function(){}, removeListener: function(){} }
    };
    Object.assign(window.chrome.runtime, runtimed);
    if (!window.chrome.loadTimes) {
        window.chrome.loadTimes = function() {
            return {
                requestTime: 0,
                startLoadTime: 0,
                commitLoadTime: 0,
                finishDocumentLoadTime: 0,
                finishLoadTime: 0,
                firstPaintTime: 0,
                firstPaintAfterLoadTime: 0,
                navigationType: 'other',
                wasFetchedViaSpdy: false,
                wasNpnNegotiated: false,
                npnNegotiatedProtocol: 'unknown',
                wasAlternateProtocolAvailable: false,
                connectionInfo: 'http/1.1'
            };
        };
    }
    if (!window.chrome.csi) {
        window.chrome.csi = function() {
            return {
                onloadT: 0,
                startE: 0,
                onloadT: 0,
                interactiveT: 0
            };
        };
    }
    if (!window.chrome.app) {
        window.chrome.app = {
            isInstalled: false,
            getIsInstalled: function(){},
            getDetails: function(){},
            InstallState: { DISABLED: 'disabled', INSTALLED: 'installed', NOT_INSTALLED: 'not_installed' },
            RunningState: { CANNOT_RUN: 'cannot_run', READY_TO_RUN: 'ready_to_run', RUNNING: 'running' }
        };
    }
})();";

    /// <summary>
    /// 伪装 Chrome Runtime（带安全来源参数）
    /// </summary>
    public const string SeleniumStealth_FakeChromeRuntime = @"
(function(allowInsecure){
    const runtimed = {
        id: 'abc123',
        lastError: undefined,
        connect: function(){},
        sendMessage: function(){},
        getManifest: function(){ return {version: '1.0', name: 'Chrome'}; },
        getURL: function(path){ return 'chrome-extension://abc123/' + path; },
        onInstalled: { addListener: function(){}, removeListener: function(){} },
        onMessage: { addListener: function(){}, removeListener: function(){} },
        onStartup: { addListener: function(){}, removeListener: function(){} },
        onSuspend: { addListener: function(){}, removeListener: function(){} },
        onSuspendCanceled: { addListener: function(){}, removeListener: function(){} },
        onUpdateAvailable: { addListener: function(){}, removeListener: function(){} },
        onConnect: { addListener: function(){}, removeListener: function(){} },
        onConnectExternal: { addListener: function(){}, removeListener: function(){} }
    };
    if (!window.chrome) window.chrome = {};
    if (!window.chrome.runtime) window.chrome.runtime = {};
    Object.assign(window.chrome.runtime, runtimed);
})();";

    /// <summary>
    /// 伪装 iFrame 代理
    /// </summary>
    public const string SeleniumStealth_iFrameProxy = @"
Object.defineProperty(window, 'length', {
    get: function() { return 0; },
    set: function(v) {}
});";

    /// <summary>
    /// 伪装 canPlayType 返回值
    /// </summary>
    public const string SeleniumStealth_FakeCanPlayType = @"
(function(){
    const orig = HTMLVideoElement.prototype.canPlayType;
    HTMLVideoElement.prototype.canPlayType = function(type) {
        if (type && type.includes('application/vnd.apple.mpegurl')) return 'probably';
        if (type && type.includes('video/webm')) return 'probably';
        return orig.call(this, type);
    };
})();";

    /// <summary>
    /// 伪装插件和 MIME 类型
    /// </summary>
    public const string SeleniumStealth_FakePluginsAndMimes = @"
(function(){
    Object.defineProperty(navigator, 'plugins', {
        get: function() {
            const arr = [1,2,3,4,5];
            arr.item = function(i){return this[i];};
            arr.namedItem = function(n){return this[0];};
            arr.refresh = function(){};
            Array.prototype.push.call(arr, {name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format', length: 1});
            Array.prototype.push.call(arr, {name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '', length: 1});
            Array.prototype.push.call(arr, {name: 'Native Client', filename: 'internal-nacl-plugin', description: '', length: 2});
            return arr;
        }
    });
    Object.defineProperty(navigator, 'mimeTypes', {
        get: function() {
            const arr = [1,2,3,4];
            arr.item = function(i){return this[i];};
            arr.namedItem = function(n){return this[0];};
            Array.prototype.push.call(arr, {type: 'application/pdf', suffixes: 'pdf', description: 'Portable Document Format', enabledPlugin: {name: 'Chrome PDF Plugin'}});
            Array.prototype.push.call(arr, {type: 'text/pdf', suffixes: 'pdf', description: 'Portable Document Format', enabledPlugin: {name: 'Chrome PDF Plugin'}});
            Array.prototype.push.call(arr, {type: 'application/x-google-chrome-print-preview-pdf', suffixes: 'pdf', description: 'Portable Document Format', enabledPlugin: {name: 'Chrome PDF Viewer'}});
            Array.prototype.push.call(arr, {type: 'application/x-nacl', suffixes: '', description: 'Native Client Executable', enabledPlugin: {name: 'Native Client'}});
            Array.prototype.push.call(arr, {type: 'application/x-nacl-sfi', suffixes: '', description: 'Native Client Executable', enabledPlugin: {name: 'Native Client'}});
            Array.prototype.push.call(arr, {type: 'application/x-pnacl', suffixes: '', description: 'Portable Native Client Executable', enabledPlugin: {name: 'Native Client'}});
            return arr;
        }
    });
})();";

    /// <summary>
    /// 伪装 window outer 尺寸
    /// </summary>
    public const string SeleniumStealth_FakeWindowOuterDimensions = @"
Object.defineProperty(window, 'outerWidth', {get: function(){return window.innerWidth + 16;}});
Object.defineProperty(window, 'outerHeight', {get: function(){return window.innerHeight + 88;}});";

    /// <summary>
    /// 隐藏 WebDriver 属性
    /// </summary>
    public const string SeleniumStealth_HideWebDriver = @"
Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
Object.defineProperty(navigator, '__webdriverFunc', {get: () => undefined});
delete navigator.webdriver;
delete navigator.__webdriverFunc;
window.navigator.webdriver = false;
Object.defineProperty(document, '$cdc_asdjflasutopfhvcZLmcfl_', {get: () => undefined});
try { delete document.$cdc_asdjflasutopfhvcZLmcfl_; } catch(e) {}";

    /// <summary>
    /// UndetectedChromeDriver 模式的核心脚本
    /// </summary>
    public const string UndetectedChromeDriver = @"
(function(){
    window.navigator.chrome = {runtime: {}};
    Object.defineProperty(navigator, 'webdriver', {get: () => undefined});
    Object.defineProperty(navigator, 'plugins', {get: () => [1,2,3,4,5]});
    Object.defineProperty(navigator, 'languages', {get: () => ['zh-CN', 'zh', 'en']});
    const originalQuery = window.navigator.permissions.query;
    window.navigator.permissions.query = (parameters) => (
        parameters.name === 'notifications' ?
            Promise.resolve({state: Notification.permission}) :
            originalQuery(parameters)
    );
})();";

    /// <summary>
    /// 伪装鼠标移动
    /// </summary>
    public const string FakeMouseMovement = @"
(function() {
    if (window.__fakeMouseMove) return;
    window.__fakeMouseMove = true;
    const event = new MouseEvent('mousemove', {
        view: window,
        bubbles: true,
        cancelable: true,
        clientX: Math.floor(Math.random() * 1000),
        clientY: Math.floor(Math.random() * 800)
    });
    document.dispatchEvent(event);
})();";

    /// <summary>
    /// 伪装 WebGL Vendor
    /// </summary>
    public const string WebGlVendor = @"
(function(vendor, renderer){
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
    if (gl) {
        const getExt = gl.getExtension.bind(gl);
        const ext = getExt('WEBGL_debug_renderer_info');
        if (ext) {
            Object.defineProperty(gl, 'getParameter', {
                value: function(p) {
                    if (p === 37445) return vendor;
                    if (p === 37446) return renderer;
                    return getExt === gl.getParameter ? gl.getParameter(p) : gl.getParameter.call(gl, p);
                }
            });
        }
    }
})();";

    /// <summary>
    /// 伪装 Navigator Vendor
    /// </summary>
    public const string NavigatorVendor = @"
(function(vendor){
    Object.defineProperty(navigator, 'vendor', {get: () => vendor});
})();";

    /// <summary>
    /// 伪装设备内存
    /// </summary>
    public const string SetDeviceMemory = @"
(function(memory){
    Object.defineProperty(navigator, 'deviceMemory', {get: () => memory});
})();";

    /// <summary>
    /// 移除 CDCD 变量
    /// </summary>
    public const string RemoveCdcVariables = @"
(function(){
    const cdc = document.getElementById('$cdc_asdjflasutopfhvcZLmcfl_');
    if (cdc) cdc.remove();
    const frame = document.getElementById('$cdc_asdjflasutopfhvcZLmcfl_-frame');
    if (frame) frame.remove();
})();";

    /// <summary>
    /// 修复细线问题
    /// </summary>
    public const string FixHairline = @"
(function(){
    const style = document.createElement('style');
    style.innerHTML = '* { -webkit-font-smoothing: antialiased; }';
    document.head.appendChild(style);
})();";

    /// <summary>
    /// 伪装加载时间
    /// </summary>
    public const string FakeLoadingTimes = @"
(function(){
    const now = Date.now();
    Object.defineProperty(document, 'timing', {
        get: function() {
            return {
                navigationStart: now - 2000,
                unloadEventStart: now - 1990,
                unloadEventEnd: now - 1980,
                redirectStart: 0,
                redirectEnd: 0,
                fetchStart: now - 1950,
                domainLookupStart: now - 1900,
                domainLookupEnd: now - 1890,
                connectStart: now - 1880,
                connectEnd: now - 1800,
                secureConnectionStart: now - 1850,
                requestStart: now - 1790,
                responseStart: now - 1700,
                responseEnd: now - 1680,
                domLoading: now - 1650,
                domInteractive: now - 1000,
                domContentLoadedEventStart: now - 900,
                domContentLoadedEventEnd: now - 850,
                domComplete: now - 100,
                loadEventStart: now - 80,
                loadEventEnd: now
            };
        }
    });
})();";
}