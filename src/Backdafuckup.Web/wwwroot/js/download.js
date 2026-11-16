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

// Saves text the page already has (a key file shown once) as a file.
window.bdfuSaveText = (name, text, type) => {
    const url = URL.createObjectURL(new Blob([text], { type: type || 'application/json' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.rel = 'noopener';
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
};
