const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

async function main() {
    const html = fs.readFileSync(path.join(__dirname, '../TVHeadEnd/Web/tvheadend.html'), 'utf8');
    const selector = html.match(/<select[^>]*id="selRecordingBackend"[\s\S]*?<\/select>/)[0];
    assert.deepEqual(Array.from(selector.matchAll(/<option value="([^"]+)"/g), match => match[1]), ['TVHeadend', 'Jellyfin']);
    const controls = new Map();
    function control(id) {
        if (!controls.has(id)) controls.set(id, { value: '', checked: false, dataset: {}, options: [], listeners: {},
            classList: { toggle() {} }, textContent: '', add(option) { this.options.push(option); },
            setAttribute() {}, querySelector: control, querySelectorAll: () => [],
            addEventListener(event, callback) { this.listeners[event] = callback; } });
        return controls.get(id);
    }
    const view = { querySelector: control, querySelectorAll: () => [], addEventListener() {} };
    const original = { TVH_ServerName: 'primary', Username: 'primary-user', Password: 'primary-password' };
    let saved;
    const context = { view, crypto: { getRandomValues: bytes => require('node:crypto').webcrypto.getRandomValues(bytes) }, Option: function (text, value) { this.text = text; this.value = value; },
        Intl: { supportedValuesOf: () => [] }, Dashboard: { showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {} },
        ApiClient: { getPluginConfiguration: async () => ({ ...original }), getUrl: url => url, ajax: async () => [],
            updatePluginConfiguration: async (_, value) => { saved = value; return {}; } } };
    const source = fs.readFileSync(path.join(__dirname, '../TVHeadEnd/Web/tvheadend.js'), 'utf8')
        .replace('export default function', 'function initialize')
        .replace('installControlTooltips(view);', 'view.loadConfig = config => loadConfig(view, config); view.renderStatus = status => renderStatus(view, status);');
    vm.runInNewContext(source + '\ninitialize(view, {});', context);
    view.loadConfig(original);
    assert.equal(control('#selRecordingBackend').value, 'TVHeadend', 'Default must keep TVHeadend DVR');
    view.loadConfig({ ...original, UseNativeTuners: true });
    assert.equal(control('#selRecordingBackend').value, 'Jellyfin');
    const store = control('#btnStoreNativeServer');
    control('#txtTVH_ServerName').value = 'second';
    control('#txtUserName').value = 'other-user';
    control('#txtPassword').value = 'other-password';
    control('#txtNativeServerName').value = '<second>';
    control('#selStreamingMethod').value = 'HttpBasic';
    store.listeners.click.call(store);
    const id = control('#selNativeServer').value;
    assert.match(id, /^[a-f0-9]{32}$/);
    control('#txtTVH_ServerName').value = 'changed';
    store.listeners.click.call(store);
    const form = control('.TVHclientConfigurationForm');
    const save = async () => { form.listeners.submit.call(form, { preventDefault() {} }); await new Promise(resolve => setImmediate(resolve)); };
    await save();
    assert.equal(saved.NativeServers.length, 1);
    assert.equal(saved.NativeServers[0].Id, id);
    assert.equal(saved.NativeServers[0].Host, 'changed');
    assert.equal(saved.NativeServers[0].Password, 'other-password');
    assert.equal(saved.NativeServers[0].StreamingMethod, 'HttpBasic');
    assert.equal(saved.TVH_ServerName, 'primary', 'Native edits must preserve integrated connection');
    view.loadConfig(saved);
    control('#selNativeServer').value = id;
    control('#selNativeServer').listeners.change.call(control('#selNativeServer'));
    assert.equal(control('#txtTVH_ServerName').value, 'changed');
    assert.equal(control('#txtPassword').value, 'other-password');
    control('#selRecordingBackend').value = 'TVHeadend';
    control('#selRecordingBackend').listeners.change.call(control('#selRecordingBackend'));
    await save();
    assert.equal(saved.UseNativeTuners, false);
    assert.equal(saved.TVH_ServerName, 'primary');
    assert.equal(saved.Password, 'primary-password');
    control('#selNativeServer').listeners.change.call(control('#selNativeServer'));
    await save();
    assert.equal(saved.TVH_ServerName, 'primary', 'Disabled native selector must not replace primary settings');
    assert.equal(saved.Password, 'primary-password');
    control('#selRecordingBackend').value = 'Jellyfin';
    control('#selRecordingBackend').listeners.change.call(control('#selRecordingBackend'));
    control('#btnRemoveNativeServer').listeners.click();
    await save();
    assert.equal(saved.NativeServers.length, 0);
    assert.equal(saved.UseNativeTuners, true);
    view.renderStatus({ UseNativeTuners: true, GeneratedUtc: new Date().toISOString(), NativeServers: [
        { Name: 'First <server>', Server: 'first:9982', Connected: true, ServerVersion: '4.3-test', HtspProtocolVersion: 44, StreamingMethod: 'Htsp' },
        { Name: 'Second server', Server: 'second:9982', Connected: false, StreamingMethod: 'HttpBasic' }
    ] });
    const summary = control('#statusSummary').innerHTML;
    assert.ok(summary.includes('first:9982 | Connected | 4.3-test | HTSP 44'), 'Status separators must use plain ASCII');
    assert.ok(summary.includes('4.3-test'), 'Native summary must retain server version');
    assert.ok(summary.includes('HTSP 44'), 'Native summary must retain negotiated protocol version');
    assert.ok(summary.includes('HttpBasic'), 'Each server must show its configured streaming method');
    assert.ok(summary.includes('First &lt;server&gt;'), 'Server names must remain escaped');
    assert.ok(summary.includes('Disconnected'));
    view.renderStatus({ UseNativeTuners: false, Connected: true, Server: 'primary:9982', ServerVersion: '4.2-test', HtspProtocolVersion: 42, StreamingMethod: 'Htsp', GeneratedUtc: new Date().toISOString() });
    assert.ok(control('#statusSummary').innerHTML.includes('4.2-test'));
    assert.ok(control('#statusSummary').innerHTML.includes('HTSP 42'));
    console.log('DVR choices, credential preservation and full native/integrated server status passed.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
