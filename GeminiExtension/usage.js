// Injected into the usage page. Return only quota fields to the extension.
export async function readVisibleUsage() {
    if (location.origin !== "https://gemini.google.com") return { type: "unavailable" };
    if (!/^\/(?:u\/\d+\/)?usage\/?$/.test(location.pathname)) {
        return { type: "signIn" };
    }

    const start = /(?:^|\n)\s*(?:用量限额|使用量限制|usage limits)\s*(?:$|\n)/i;
    const weeklyLabel = /每周限额|每週上限|weekly (?:usage )?limit/i;
    const resetTime = /(?:重[置設]时间|重設時間|resets?(?:\s+(?:on|at))?)\s*[:：]?\s*((?:(?:\d{1,2}\s*月\s*\d{1,2}\s*日|\d{1,2}\/\d{1,2}|(?:[a-z]{3,9},?\s+)?[a-z]{3,9}\s+\d{1,2})\s*(?:,\s*|at\s+)?\s*)?(?:上午|下午)?\s*\d{1,2}:\d{2}(?:\s*[AP]M)?)/i;

    function parse(text) {
        const used = text.match(/(?:已使用\s*(\d{1,3})\s*%|(\d{1,3})\s*%\s*used)/i);
        if (!used) return null;
        const value = Number(used[1] ?? used[2]);
        if (value > 100) return null;
        const reset = text.match(resetTime)?.[1].trim() ?? null;
        return { used: value, reset };
    }

    const deadline = Date.now() + 9_000;
    while (Date.now() < deadline) {
        const main = document.querySelector("main, [role=main]");
        const mainText = main?.innerText ?? "";
        const text = start.test(mainText) ? mainText : document.body?.innerText ?? "";
        const heading = start.exec(text);
        if (heading) {
            const section = text.slice(heading.index + heading[0].length);
            const weeklyIndex = section.search(weeklyLabel);
            if (weeklyIndex >= 0) {
                const current = parse(section.slice(0, weeklyIndex));
                const weekly = parse(section.slice(weeklyIndex));
                if (current && weekly) return { type: "usage", current, weekly };
            }
        }
        if (document.querySelector('a[href^="https://accounts.google.com/ServiceLogin"]'))
            return { type: "signIn" };
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    return { type: "unavailable" };
}
