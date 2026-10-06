const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

async function main() {
    const controls = new Map();
    const view = { querySelector: control, querySelectorAll: () => [], addEventListener() {} };
    function control(id) {
        if (!controls.has(id)) controls.set(id, { value: '', checked: false, dataset: {}, options: [], listeners: {}, classList: { toggle() {} },
            textContent: '', add() {}, appendChild() {}, setAttribute() {}, querySelector: control,
            querySelectorAll: () => [], addEventListener(name, handler) { this.listeners[name] = handler; } });
        return controls.get(id);
    }
    let saved;
    const context = { view, Option: function () {}, Intl: { supportedValuesOf: () => [] }, Dashboard: {
        showLoadingMsg() {}, hideLoadingMsg() {}, processPluginConfigurationUpdateResult() {}
    }, ApiClient: {
        getPluginConfiguration: async () => ({}), getUrl: value => value, ajax: async () => [],
        updatePluginConfiguration: async (_, config) => { saved = config; return {}; }
    } };
    const source = fs.readFileSync(path.join(__dirname, '../TVHeadEnd/Web/tvheadend.js'), 'utf8')
        .replace('export default function', 'function initialize')
        .replace('installControlTooltips(view);', 'view.loadConfig = config => loadConfig(view, config);');
    vm.runInNewContext(source + '\ninitialize(view, {});', context);
    const checkbox = control('#chkGenerateMissingProgrammeImages');
    const background = control('#chkCaptureUnwatchedProgrammeImages');
    view.loadConfig({});
    assert.equal(checkbox.checked, false, 'Missing saved setting must default off');
    assert.equal(background.checked, false);
    view.loadConfig({ GenerateMissingProgrammeImages: true, CaptureUnwatchedProgrammeImages: true });
    assert.equal(checkbox.checked, true);
    assert.equal(background.checked, true);
    const form = control('.TVHclientConfigurationForm');
    for (const enabled of [true, false]) {
        checkbox.checked = enabled;
        background.checked = enabled;
        form.listeners.submit.call(form, { preventDefault() {} });
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(saved.GenerateMissingProgrammeImages, enabled);
        assert.equal(saved.CaptureUnwatchedProgrammeImages, enabled);
    }
    console.log('Programme artwork checkbox loads defaults and saves both enabled and disabled states.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
