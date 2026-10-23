function escapeHtml(value) {
  return String(value == null ? '' : value).replace(/[&<>"']/g, char => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
  })[char]);
}

export function installAdminNavigation(root, getCommands) {
  if (root.dataset.adminNavigationHandlers === 'true') return;
  root.dataset.adminNavigationHandlers = 'true';

  let returnFocus = null;
  let selectedIndex = 0;

  const closePalette = (restoreFocus = true) => {
    root.querySelector('.admin-command-backdrop')?.remove();
    if (!restoreFocus) return;
    const target = returnFocus?.isConnected
      ? returnFocus
      : root.querySelector('[data-admin-command-open]');
    target?.focus();
  };

  const updateResults = () => {
    const input = root.querySelector('[data-admin-command-input]');
    const list = root.querySelector('[data-admin-command-results]');
    if (!input || !list) return [];
    const query = input.value.trim().toLocaleLowerCase('zh-CN');
    const commands = getCommands().filter(command =>
      `${command.label} ${command.description}`.toLocaleLowerCase('zh-CN').includes(query));
    selectedIndex = commands.length
      ? Math.min(Math.max(selectedIndex, 0), commands.length - 1)
      : 0;
    list.innerHTML = commands.length ? commands.map((command, index) =>
      `<a id="admin-command-${index}" class="admin-command-result${index === selectedIndex ? ' active' : ''}" ` +
      `href="${escapeHtml(command.path)}" data-route="${escapeHtml(command.path)}" ` +
      `role="option" aria-selected="${index === selectedIndex}" tabindex="-1">` +
      `<span><strong>${escapeHtml(command.label)}</strong><small>${escapeHtml(command.description)}</small></span>` +
      '<span class="admin-command-enter" aria-hidden="true">↵</span></a>').join('')
      : '<p class="admin-command-empty">没有匹配的管理页面。</p>';
    input.setAttribute('aria-expanded', String(commands.length > 0));
    if (commands.length) input.setAttribute('aria-activedescendant', `admin-command-${selectedIndex}`);
    else input.removeAttribute('aria-activedescendant');
    return commands;
  };

  const openPalette = trigger => {
    if (root.querySelector('.admin-command-backdrop')) return;
    if (!getCommands().length) return;
    returnFocus = trigger;
    selectedIndex = 0;
    root.insertAdjacentHTML('beforeend',
      '<div class="admin-command-backdrop" data-admin-command-backdrop>' +
      '<section class="admin-command-panel" role="dialog" aria-modal="true" aria-labelledby="admin-command-title">' +
      '<label class="admin-command-search" for="admin-command-input"><span aria-hidden="true">⌕</span>' +
      '<input id="admin-command-input" data-admin-command-input type="search" role="combobox" ' +
      'aria-autocomplete="list" aria-controls="admin-command-results" aria-expanded="false" ' +
      'placeholder="输入页面名称…" autocomplete="off" />' +
      '<kbd>Esc</kbd></label><h2 id="admin-command-title" class="sr-only">查找管理页面</h2>' +
      '<div id="admin-command-results" class="admin-command-results" data-admin-command-results ' +
      'role="listbox" aria-label="管理页面搜索结果"></div>' +
      '<p class="admin-command-hint">方向键选择 · Enter 打开 · Ctrl / ⌘ K 搜索</p></section></div>');
    updateResults();
    root.querySelector('[data-admin-command-input]')?.focus();
  };

  root.addEventListener('click', event => {
    const openButton = event.target.closest('[data-admin-command-open]');
    if (openButton) {
      event.preventDefault();
      openPalette(openButton);
      return;
    }
    const sidebarToggle = event.target.closest('[data-admin-sidebar-toggle]');
    if (sidebarToggle) {
      const sidebar = root.querySelector('.sidebar');
      const overlay = root.querySelector('.mobile-sidebar-overlay');
      const isOpen = sidebar?.classList.toggle('open') ?? false;
      overlay?.classList.toggle('open', isOpen);
      root.querySelector('[data-admin-sidebar-toggle][aria-controls="admin-sidebar"]')
        ?.setAttribute('aria-expanded', String(isOpen));
      if (isOpen) sidebar?.querySelector('.side-nav a')?.focus();
      else root.querySelector('[data-admin-sidebar-toggle][aria-controls="admin-sidebar"]')?.focus();
      return;
    }
    if (event.target.matches('[data-admin-command-backdrop]')) {
      closePalette();
      return;
    }
    if (event.target.closest('.admin-command-backdrop a[data-route]')) closePalette(false);
    if (event.target.closest('[data-route]')) {
      root.querySelector('.sidebar')?.classList.remove('open');
      root.querySelector('.mobile-sidebar-overlay')?.classList.remove('open');
    }
  });

  root.addEventListener('input', event => {
    if (!event.target.matches('[data-admin-command-input]')) return;
    selectedIndex = 0;
    updateResults();
  });

  root.addEventListener('keydown', event => {
    if (!root.querySelector('.admin-command-backdrop')) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      closePalette();
      return;
    }
    if (event.key === 'Tab') {
      event.preventDefault();
      root.querySelector('[data-admin-command-input]')?.focus();
      return;
    }
    if (!event.target.matches('[data-admin-command-input]')) return;
    const commands = updateResults();
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!commands.length) return;
      selectedIndex = (selectedIndex + (event.key === 'ArrowDown' ? 1 : commands.length - 1)) % commands.length;
      updateResults();
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      root.querySelector(`#admin-command-${selectedIndex}`)?.click();
    }
  });

  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (root.querySelector('.admin-command-backdrop')) closePalette();
      else openPalette(root.querySelector('[data-admin-command-open]'));
      return;
    }
    if (event.key === 'Escape' && root.querySelector('.admin-command-backdrop')) {
      event.preventDefault();
      closePalette();
    }
  });
}
