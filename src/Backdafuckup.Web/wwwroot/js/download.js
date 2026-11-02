// Starts a browser download from a same-origin link without leaving the page (the auth cookie goes along).
window.bdfuDownload = (url) => {
    const link = document.createElement('a');
    link.href = url;
    link.download = '';
    link.rel = 'noopener';
    document.body.appendChild(link);
    link.click();
    link.remove();
};
