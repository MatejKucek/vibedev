'use strict';
const content = document.getElementById('content');
const announcement = document.getElementById('announcement');
let currentVersion = 0;
const base = document.createElement('base');
document.head.append(base);

chrome.webview.addEventListener('message', ({ data }) => {
  if (data.type !== 'render' || data.version < currentVersion) return;
  currentVersion = data.version;
  const scroll = window.scrollY;
  base.href = data.baseUri;
  content.innerHTML = data.html || '<p class="empty">This Markdown file is empty. Choose Edit to start writing.</p>';
  for (const table of content.querySelectorAll('table')) {
    const wrap = document.createElement('div');
    wrap.className = 'table-wrap'; table.replaceWith(wrap); wrap.append(table);
  }
  for (const pre of content.querySelectorAll('pre')) {
    const code = pre.querySelector('code');
    const language = [...(code?.classList || [])].find(name => name.startsWith('language-'));
    pre.dataset.language = language?.slice(9) || 'Code';
    const copy = document.createElement('button'); copy.className = 'copy-code'; copy.textContent = 'Copy'; copy.type = 'button';
    copy.addEventListener('click', () => chrome.webview.postMessage({ type: 'copy', text: code?.textContent || '' }));
    pre.append(copy);
  }
  for (const img of content.querySelectorAll('img')) {
    const unavailable = () => {
      const fallback = document.createElement('p'); fallback.className = 'image-unavailable';
      fallback.textContent = img.alt ? `Image: ${img.alt}` : 'Image unavailable'; img.replaceWith(fallback);
    };
    img.addEventListener('error', unavailable, { once: true });
    if (img.complete && !img.naturalWidth) unavailable();
  }
  requestAnimationFrame(() => {
    window.scrollTo(0, scroll);
    chrome.webview.postMessage({ type: 'rendered', version: data.version });
  });
});

content.addEventListener('click', event => {
  const link = event.target.closest('a');
  if (!link) return;
  event.preventDefault();
  const href = link.getAttribute('href');
  if (!href) return;
  if (href.startsWith('#')) {
    try { document.getElementById(decodeURIComponent(href.slice(1)))?.scrollIntoView(); } catch { /* Invalid anchor. */ }
    return;
  }
  chrome.webview.postMessage({ type: 'link', url: link.href });
});

chrome.webview.addEventListener('message', ({ data }) => {
  if (data.type !== 'copied') return;
  announcement.textContent = 'Code copied';
  setTimeout(() => { announcement.textContent = ''; }, 1800);
});
chrome.webview.postMessage({ type: 'ready' });
