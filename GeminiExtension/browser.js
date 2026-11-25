import { readVisibleUsage } from "./usage.js";

const host = "com.quotamonitor.gemini";
const usageUrl = "https://gemini.google.com/usage";
const site = { origins: ["https://gemini.google.com/*"] };
const retry = "reconnect";
let port;
let checking = false;

async function connect() {
    if (port || !await chrome.permissions.contains(site) || port) return;
    const connection = chrome.runtime.connectNative(host);
    port = connection;
    connection.onMessage.addListener(message => {
        if (message.type === "refresh") void refresh(connection);
    });
    connection.onDisconnect.addListener(() => {
        const error = chrome.runtime.lastError?.message;
        if (port === connection) port = undefined;
        void chrome.action.setTitle({ title: "Gemini · 等待额度监控器连接" });
        if (error !== "Specified native messaging host not found.")
            void chrome.alarms.create(retry, { delayInMinutes: 1 });
    });
}

chrome.runtime.onStartup.addListener(() => void connect());
chrome.runtime.onInstalled.addListener(() => void connect());
chrome.alarms.onAlarm.addListener(alarm => {
    if (alarm.name === retry) void connect();
});
chrome.permissions.onRemoved.addListener(() => {
    void chrome.permissions.contains(site).then(granted => {
        if (granted || !port) return;
        port.postMessage({ type: "permission" });
        port.disconnect();
        port = undefined;
    });
});
chrome.action.onClicked.addListener(async () => {
    if (!await chrome.permissions.request(site)) return;
    if (port) await refresh(port);
    else await connect();
});

async function refresh(connection) {
    if (checking) return;
    checking = true;
    let ownedTab;
    try {
        if (!await chrome.permissions.contains(site)) {
            connection.postMessage({ type: "permission" });
            return;
        }
        ownedTab = await chrome.tabs.create({ url: usageUrl, active: false });
        const result = await waitForUsage(ownedTab.id);
        if (port !== connection) return;
        connection.postMessage(result);
        const title = result.type === "usage" ? "刷新 Gemini 额度" :
            result.type === "signIn" ? "请在 Chrome 登录 Gemini" : "暂时读不到 Gemini 用量";
        await chrome.action.setTitle({ title });
    } catch {
        if (port === connection) connection.postMessage({ type: "unavailable" });
    } finally {
        if (ownedTab?.id !== undefined) {
            try { await chrome.tabs.remove(ownedTab.id); } catch { /* The tab may already be closed. */ }
        }
        checking = false;
    }
}

async function waitForUsage(tabId) {
    const deadline = Date.now() + 18_000;
    while (Date.now() < deadline) {
        const tab = await chrome.tabs.get(tabId);
        if (tab.status === "complete") {
            if (!tab.url?.startsWith("https://gemini.google.com/")) return { type: "signIn" };
            const [injection] = await chrome.scripting.executeScript({
                target: { tabId },
                func: readVisibleUsage
            });
            return injection?.result ?? { type: "unavailable" };
        }
        await new Promise(resolve => setTimeout(resolve, 200));
    }
    return { type: "unavailable" };
}
