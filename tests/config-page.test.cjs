const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const html = fs.readFileSync(path.join(__dirname, '../src/Jellyfin.Plugin.AutoOrganizer/Configuration/configPage.html'), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const flush = () => new Promise(setImmediate);

class Element {
    value = ''; children = []; handlers = {}; textContent = ''; disabled = false; hidden = true;
    get options() { return this.children; }
    appendChild(child) { this.children.push(child); }
    replaceChildren() { this.children = []; }
    addEventListener(name, handler) { this.handlers[name] = handler; }
    emit(name, event = {}) { return this.handlers[name]?.call(this, { preventDefault() {}, ...event }); }
    focus() {} scrollIntoView() {}
}

function setup({ config = {}, api = {} } = {}) {
    const fields = Object.fromEntries([...html.matchAll(/\bid="([^"]+)"/g)].map(match => [match[1], new Element()]));
    const form = fields.AutoOrganizerForm;
    const page = fields.AutoOrganizerPage;
    page.querySelector = selector => {
        const element = selector === 'form' ? form : fields[selector.slice(1)];
        assert.ok(element, `Missing HTML element ${selector}`);
        return element;
    };
    const errors = []; const saved = []; const directoryCalls = [];
    const configuration = { Enabled: true, DryRun: true, IncludeSeriesYear: false, IncomingPath: '/downloads/completed', LibraryPath: '/media', StableSeconds: 300, Mode: 'Copy', ...config };
    const ApiClient = {
        getPluginConfiguration: async () => ({ ...configuration }),
        updatePluginConfiguration: async (id, value) => { saved.push(value); return {}; },
        getVirtualFolders: async () => [
            { Name: 'Series', CollectionType: 'tvshows', Locations: ['/media/TV', '/other/TV'] },
            { Name: 'Films', CollectionType: 'movies', Locations: ['/films'] },
            { Name: 'Music', CollectionType: 'music', Locations: ['/music'] }
        ],
        getDrives: async () => [{ Name: '/', Path: '/' }],
        getDirectoryContents: async (directory, options) => {
            directoryCalls.push({ directory, options });
            return directory === '/' ? [{ Name: 'downloads', Path: '/downloads' }] : [];
        },
        getParentPath: async () => JSON.stringify('/'),
        ...api
    };
    vm.runInNewContext(script, {
        document: { querySelector: () => page, createElement: () => new Element() },
        ApiClient,
        Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, alert: message => errors.push(message), processPluginConfigurationUpdateResult() {} }
    });
    return { fields, page, form, errors, saved, directoryCalls };
}

test('existing libraries list physical folders, filter media types and retain old configuration', async () => {
    const { page, fields, errors } = setup();
    page.emit('pageshow'); await flush();
    assert.equal(fields.LibraryPath.value, '/media');
    assert.equal(fields.ShowsPath.value, '');
    assert.deepEqual(fields.ShowsLibrary.options.map(option => option.value), ['', '/media/TV', '/other/TV']);
    assert.deepEqual(fields.MoviesLibrary.options.map(option => option.value), ['', '/films']);
    assert.ok(fields.RootSuggestions.children.some(option => option.value === '/media'));
    assert.deepEqual(errors, []);
});

test('existing library selections save exact paths without a shared root', async () => {
    const { page, fields, form, saved } = setup();
    page.emit('pageshow'); await flush();
    fields.ShowsLibrary.value = '/media/TV'; fields.ShowsLibrary.emit('change');
    fields.MoviesLibrary.value = '/films'; fields.MoviesLibrary.emit('change');
    fields.LibraryPath.value = '';
    form.emit('submit'); await flush();
    assert.equal(saved[0].ShowsPath, '/media/TV');
    assert.equal(saved[0].MoviesPath, '/films');
    assert.equal(saved[0].LibraryPath, '');
    assert.equal(saved[0].DryRun, true);
});

test('browser navigates server roots and selects an incoming folder without saving automatically', async () => {
    const { page, fields, saved, directoryCalls } = setup({ config: { IncomingPath: '' } });
    page.emit('pageshow'); await flush();
    fields.BrowseIncomingPath.emit('click'); await flush();
    assert.equal(fields.BrowserChoose.disabled, true);
    fields.BrowserFolders.children[0].emit('click'); await flush();
    fields.BrowserFolders.children[0].emit('click'); await flush();
    assert.equal(fields.BrowserPath.value, '/downloads');
    fields.BrowserChoose.emit('click');
    assert.equal(fields.IncomingPath.value, '/downloads');
    assert.equal(fields.FolderBrowser.hidden, true);
    assert.equal(saved.length, 0);
    assert.equal(directoryCalls[0].options.includeFiles, false);
    assert.equal(directoryCalls[0].options.includeDirectories, true);
});

test('cancel and unreadable paths preserve existing settings', async () => {
    const { page, fields } = setup({ api: { getDirectoryContents: async () => { throw new Error('forbidden'); } } });
    page.emit('pageshow'); await flush();
    fields.BrowseIncomingPath.emit('click'); await flush();
    assert.equal(fields.BrowserChoose.disabled, true);
    assert.match(fields.BrowserStatus.textContent, /permissions/);
    fields.BrowserCancel.emit('click');
    assert.equal(fields.IncomingPath.value, '/downloads/completed');
});

test('typing a path disables the previous selection until it has been opened', async () => {
    const { page, fields } = setup();
    page.emit('pageshow'); await flush();
    fields.BrowseShowsPath.emit('click'); await flush();
    fields.BrowserPath.value = '/new/tv'; fields.BrowserPath.emit('input');
    assert.equal(fields.BrowserChoose.disabled, true);
    fields.BrowserChoose.emit('click');
    assert.equal(fields.ShowsPath.value, '');
    fields.BrowserOpen.emit('click'); await flush();
    fields.BrowserChoose.emit('click');
    assert.equal(fields.ShowsPath.value, '/new/tv');
});

test('late browser responses cannot replace a newer navigation', async () => {
    let resolveOld;
    const { page, fields } = setup({ api: { getDirectoryContents: () => new Promise(resolve => { resolveOld = resolve; }) } });
    page.emit('pageshow'); await flush();
    fields.BrowseIncomingPath.emit('click'); await flush();
    fields.BrowserRoots.emit('click'); await flush();
    resolveOld([{ Name: 'stale', Path: '/stale' }]); await flush();
    assert.equal(fields.BrowserFolders.children[0].textContent, '/');
    assert.equal(fields.BrowserChoose.disabled, true);
});

test('parent navigation handles Jellyfin JSON-encoded paths', async () => {
    const { page, fields } = setup();
    page.emit('pageshow'); await flush();
    fields.BrowseIncomingPath.emit('click'); await flush();
    fields.BrowserUp.emit('click'); await flush();
    assert.equal(fields.BrowserPath.value, '/');
});

test('library discovery failure leaves custom paths usable', async () => {
    const { page, fields, form, saved, errors } = setup({ api: { getVirtualFolders: async () => { throw new Error('offline'); } } });
    page.emit('pageshow'); await flush();
    assert.match(fields.LibraryStatus.textContent, /Could not load/);
    form.emit('submit'); await flush();
    assert.equal(saved[0].LibraryPath, '/media');
    assert.deepEqual(errors, []);
});

test('missing shared root requires both destination folders before saving', async () => {
    const { page, fields, form, saved, errors } = setup();
    page.emit('pageshow'); await flush();
    fields.LibraryPath.value = ''; fields.ShowsPath.value = '/tv';
    form.emit('submit'); await flush();
    assert.equal(saved.length, 0);
    assert.match(errors[0], /both Shows and Movies/);
});

test('mixed libraries and PathInfos are offered without rendering names as HTML', async () => {
    const { page, fields } = setup({ api: { getVirtualFolders: async () => [{ Name: '<img src=x onerror=alert(1)>', LibraryOptions: { PathInfos: [{ Path: '/mixed' }] } }] } });
    page.emit('pageshow'); await flush();
    assert.equal(fields.ShowsLibrary.options[1].value, '/mixed');
    assert.equal(fields.MoviesLibrary.options[1].value, '/mixed');
    assert.match(fields.ShowsLibrary.options[1].textContent, /^<img/);
    assert.equal(fields.ShowsLibrary.options[1].children.length, 0);
});
