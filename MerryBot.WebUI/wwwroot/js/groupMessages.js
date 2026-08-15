// 群聊消息页与转发消息模态框的脚本（从 GroupMessages.razor 内联 script 抽离）
window.amrPlayers = {};
window.playAmrAudio = function (url) {
    if (window.amrPlayers[url]) {
        var player = window.amrPlayers[url];
        if (player.isPlaying()) {
            player.stop();
        } else {
            player.play();
        }
        return;
    }
    var amr = new BenzAMRRecorder();
    window.amrPlayers[url] = amr;
    amr.initWithUrl(url).then(function () {
        amr.play();
    });
    amr.onEnded(function () {
    });
};

window.openForwardModal = function (forwardId) {
    const modal = document.getElementById('forward-modal');
    const iframe = document.getElementById('forward-iframe');
    if (!modal || !iframe) return;
    iframe.src = '/forwardedmessage?id=' + encodeURIComponent(forwardId) + '&embed=1';
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
};

window.closeForwardModal = function () {
    const modal = document.getElementById('forward-modal');
    const iframe = document.getElementById('forward-iframe');
    // 无条件还原 body 滚动，避免异常时整页被锁死
    document.body.style.overflow = '';
    if (modal) modal.style.display = 'none';
    if (iframe) iframe.src = '';
};

// 导航菜单：移动端点击后关闭展开的 checkbox 菜单
window.closeNavMenu = function () {
    const toggler = document.querySelector('.nav-toggler');
    if (toggler && toggler.checked) {
        toggler.checked = false;
    }
};
