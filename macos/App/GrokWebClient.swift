import AppKit
import SwiftUI
import WebKit

enum GrokFailure: Error {
    case loginRequired, unavailable, noReading, connection, rateLimited(TimeInterval)

    var message: String {
        switch self {
        case .loginRequired: "Grok 登录已失效或尚未登录。请在官方页面重新登录。"
        case .unavailable: "Grok 拒绝了页面访问。可以在连接窗口检查登录或网页验证。"
        case .noReading: "Grok 页面没有显示可识别的周额度。请在连接窗口确认 Settings → Usage 中存在周用量卡片。"
        case .connection: "未能加载 Grok 用量页。请检查网络后再试。"
        case .rateLimited: "Grok 暂时限制查询频率。"
        }
    }
}

@MainActor
final class GrokWebClient: NSObject, WKNavigationDelegate, WKUIDelegate {
    private let dataStore: WKWebsiteDataStore
    private var background: WKWebView?
    private var interactive: WKWebView?
    private var popup: NSWindow?
    private var failure: GrokFailure?

    init(profile: UUID) {
        dataStore = WKWebsiteDataStore(forIdentifier: profile)
        super.init()
    }

    private func makeView() -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = dataStore
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 980, height: 740), configuration: configuration)
        view.navigationDelegate = self
        view.uiDelegate = self
        return view
    }

    func openLogin(signInFirst: Bool) -> WKWebView {
        if let interactive { return interactive }
        let view = makeView()
        interactive = view
        navigate(signIn: signInFirst)
        return view
    }

    func navigate(signIn: Bool) {
        var url = Feed.grok.url
        if signIn {
            var components = URLComponents()
            components.scheme = "https"
            components.host = "grok.com"
            components.path = "/sign-in"
            components.queryItems = [URLQueryItem(name: "return_to", value: "/?_s=usage")]
            url = components.url ?? url
        }
        interactive?.load(URLRequest(url: url))
    }

    func endLogin() {
        popup?.close()
        popup = nil
        interactive?.stopLoading()
        interactive = nil
    }

    func cancelRefresh() {
        background?.stopLoading()
        background = nil
    }

    func refresh() async throws -> GrokUsage {
        let view = makeView()
        background = view
        failure = nil
        defer {
            view.stopLoading()
            if background === view { background = nil }
        }
        view.load(URLRequest(url: Feed.grok.url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 23))

        var found: GrokUsage?
        for _ in 0..<28 {
            try await Task.sleep(for: .milliseconds(850))
            if let failure { throw failure }
            guard !view.isLoading, let url = view.url else { continue }
            if ["auth.x.ai", "accounts.x.ai"].contains(url.host?.lowercased() ?? "") {
                throw GrokFailure.loginRequired
            }
            guard Self.isGrokPage(url) else { throw GrokFailure.unavailable }
            let result = try? await view.callAsyncJavaScript(
                Self.inspectUsage, arguments: ["weeklyHeadingPattern": GrokUsage.weeklyHeadingPattern],
                in: nil, contentWorld: .defaultClient
            )
            try Task.checkCancellation()
            guard let fields = result as? [String: String] else { continue }
            if fields["status"] == "login" { throw GrokFailure.loginRequired }
            if fields["status"] == "challenge" { throw GrokFailure.unavailable }
            if let card = fields["card"],
               let reading = GrokUsage(card: card, dateTime: fields["dateTime"],
                                       includedPercent: fields["includedPercent"].flatMap(Double.init), now: .now) {
                if reading.percentUsed != nil && reading.reset != nil { return reading }
                found = reading
            }
        }
        if let found { return found }
        if view.url == nil || view.isLoading { throw GrokFailure.connection }
        throw GrokFailure.noReading
    }

    private static func isGrokPage(_ url: URL) -> Bool {
        url.scheme == "https" && ["grok.com", "www.grok.com"].contains(url.host?.lowercased() ?? "")
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse) async -> WKNavigationResponsePolicy {
        guard webView === background, navigationResponse.isForMainFrame,
              let response = navigationResponse.response as? HTTPURLResponse else { return .allow }
        switch response.statusCode {
        case 401: failure = .loginRequired
        case 403: failure = .unavailable
        case 429:
            let header = response.value(forHTTPHeaderField: "Retry-After")
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
            let delay = header.flatMap(Double.init) ?? header.flatMap { formatter.date(from: $0)?.timeIntervalSinceNow } ?? 60
            failure = .rateLimited(delay.isFinite ? max(1, delay) : 60)
        case 500...599: failure = .connection
        default: break
        }
        return failure == nil ? .allow : .cancel
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if webView === background && failure == nil { failure = .connection }
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        if webView === background && failure == nil { failure = .connection }
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        guard webView !== background, interactive != nil, navigationAction.request.url?.scheme == "https" else { return nil }
        popup?.close()
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.uiDelegate = self
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 640),
                              styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.title = "Grok 官方登录"
        window.contentView = view
        window.center()
        window.makeKeyAndOrderFront(nil)
        popup = window
        return view
    }

    func webViewDidClose(_ webView: WKWebView) {
        if popup?.contentView === webView {
            popup?.close()
            popup = nil
        }
    }

    static let inspectUsage = #"""
    const visible = node => node instanceof HTMLElement &&
        node.getClientRects().length > 0 && getComputedStyle(node).visibility !== "hidden";
    const clean = text => (text ?? "").trim().replace(/\s+/g, " ");
    const weekly = new RegExp(weeklyHeadingPattern, "i");
    const reset = /\breset(?:s|ting)?\b|重[置设設]/i;
    const usage = /^(usage|用量|使用情况|使用情況|使用量)$/i;
    const sectionTitles = 'h1, h2, h3, h4, h5, h6, [role="heading"]';
    const usageTitles = sectionTitles + ', button, a, [role="tab"]';
    const used = /\bused\b|已(?:使用|用)|剩[余餘]|\b(?:remaining|left)\b/i;
    const openOnUsage = new URL(location.href).searchParams.get("_s") === "usage" ||
        /^\/(?:settings\/usage|usage)(?:\/|$)/.test(location.pathname);
    const hasUsageTitle = node => [...node.querySelectorAll(usageTitles)].some(child =>
        visible(child) && usage.test(clean(child.textContent)));
    const dialog = [...document.querySelectorAll('dialog[open], [role="dialog"]')].find(node =>
        visible(node) && hasUsageTitle(node));
    const root = dialog ?? (openOnUsage && document.body && hasUsageTitle(document.body) ? document.body : null);
    if (root) {
        const headings = [...root.querySelectorAll(sectionTitles)].filter(node =>
            visible(node) && weekly.test(clean(node.textContent)));
        for (const heading of headings) {
            for (let card = heading.parentElement; card && card !== document.body; card = card.parentElement) {
                const content = card.innerText?.trim() ?? "";
                if (content.length > 1400) break;
                const title = clean(heading.textContent);
                const index = content.indexOf(title);
                if (index < 0) continue;
                const details = content.slice(index + title.length).trim();
                if (!reset.test(details)) continue;
                const gauges = [...card.querySelectorAll('progress, [role="progressbar"]')].filter(node =>
                    visible(node) && /^(included usage|包含的使用量)$/i.test(clean(node.getAttribute("aria-label"))));
                let includedPercent = "";
                if (gauges.length === 1) {
                    const gauge = gauges[0];
                    const native = gauge instanceof HTMLProgressElement;
                    const value = native ? gauge.getAttribute("value") : gauge.getAttribute("aria-valuenow");
                    const max = native ? gauge.max : Number(gauge.getAttribute("aria-valuemax") ?? 100);
                    const min = native ? 0 : Number(gauge.getAttribute("aria-valuemin") ?? 0);
                    const percent = (Number(value) - min) / (max - min) * 100;
                    if (value !== null && value.trim() && percent >= 0 && percent <= 100) {
                        includedPercent = String(percent);
                    }
                }
                if (!includedPercent && !used.test(details) && !/[%％]/.test(details)) continue;
                const times = [...card.querySelectorAll("time[datetime]")].filter(visible);
                const dateTime = times.length === 1 ? times[0].getAttribute("datetime") ?? "" : "";
                return {card: title + "\n" + details, dateTime, includedPercent};
            }
        }
    }
    const title = clean(document.title);
    if (/just a moment|attention required|checking your browser|verify you are human/i.test(title)) {
        return {status: "challenge"};
    }
    const login = [...document.querySelectorAll("button, a")].some(node =>
        visible(node) && /^(log in|sign in|登录|登入|登入帳號|ログイン)$/i.test(clean(node.textContent)));
    return login && !(root && /weekly|本周|每周/i.test(root.innerText.slice(0, 1800))) ? {status: "login"} : {};
    """#
}

struct GrokWebsite: NSViewRepresentable {
    let client: GrokWebClient
    let signInFirst: Bool
    func makeNSView(context: Context) -> WKWebView { client.openLogin(signInFirst: signInFirst) }
    func updateNSView(_ view: WKWebView, context: Context) {}
}
