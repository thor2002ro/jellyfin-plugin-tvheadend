const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

async function main() {
    const controls = new Map();
    function control(id) {
        if (!controls.has(id)) controls.set(id, {
            value: '', disabled: false, checked: false, textContent: '', listeners: {},
            attributes: {}, setAttribute(name, value) { this.attributes[name] = value; },
            addEventListener(name, handler) { this.listeners[name] = handler; }
        });
        return controls.get(id);
    }
    const view = { querySelector: control, querySelectorAll: () => [], addEventListener() {} };
    const requests = [];
    const context = { view, ApiClient: {
        getUrl: value => value,
        ajax: options => new Promise((resolve, reject) => requests.push({ options, resolve, reject }))
    } };
    const source = fs.readFileSync(path.join(__dirname, '../TVHeadEnd/Web/tvheadend.js'), 'utf8');
    vm.runInNewContext(source.replace('export default function', 'function initialize') + '\ninitialize(view, {});', context);
    control('#txtTVH_ServerName').value = ' unsaved.example ';
    control('#txtHTTP_Port').value = '19981';
    control('#txtHTSP_Port').value = '19982';
    control('#txtWebRoot').value = '/proxy';
    control('#txtUserName').value = 'unsaved user';
    control('#txtPassword').value = 'unsaved credential';
    control('#chkUseHttps').checked = true;
    const button = control('#btnTestConnection');
    const result = control('#connectionTestResult');
    const settle = () => new Promise(resolve => setImmediate(resolve));
    for (const method of ['Htsp', 'HttpTicket', 'HttpBasic']) {
        control('#selStreamingMethod').value = method;
        button.listeners.click.call(button);
        const count = requests.length;
        button.listeners.click.call(button);
        assert.equal(requests.length, count, 'Duplicate click started another test');
        assert.equal(button.disabled, true);
        const request = requests.at(-1);
        assert.equal(request.options.url, 'TVHeadEnd/Configuration/TestConnection');
        const payload = JSON.parse(request.options.data);
        assert.equal(payload.StreamingMethod, method);
        assert.equal(payload.TVH_ServerName, 'unsaved.example');
        assert.equal(payload.Password, 'unsaved credential');
        assert.equal(payload.HTTP_Port, 19981);
        assert.equal(payload.UseHttps, true);
        request.resolve({ Success: true, Message: 'Connected <example>' });
        await settle();
        assert.equal(button.disabled, false);
        assert.equal(result.textContent, 'Success: Connected <example>');
        assert.equal(result.attributes['aria-busy'], 'false');
    }
    button.listeners.click.call(button);
    requests.at(-1).resolve({ Success: false, Message: 'Access denied' });
    await settle();
    assert.equal(result.textContent, 'Failed: Access denied');
    button.listeners.click.call(button);
    requests.at(-1).reject(new Error('Network failure'));
    await settle();
    assert.equal(button.disabled, false);
    assert.match(result.textContent, /Unable to run/);
    console.log('Connection button checks passed for all streaming methods, duplicate clicks and failures.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
