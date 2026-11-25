import Foundation

enum AntigravityFailure: Error {
    case missingCLI, login, timeout, noReading, unavailable

    var message: String {
        switch self {
        case .missingCLI: "未找到 agy。请安装 Google Antigravity CLI 后重试。"
        case .login: "请用同一 Google AI Pro 账号登录 Antigravity CLI，然后点刷新。"
        case .timeout: "额度查询超时，请稍后刷新。"
        case .noReading: "CLI 未返回可识别的额度。请在 Antigravity CLI 中用 /usage 核对。"
        case .unavailable: "Antigravity CLI 未完成查询。请检查连接，或打开 CLI 并运行 /usage。"
        }
    }
}

@MainActor
final class AntigravityClient {
    static var executable: URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let candidates = [home.appendingPathComponent(".local/bin/agy"),
                          home.appendingPathComponent(".gemini/antigravity/bin/agy"),
                          URL(fileURLWithPath: "/opt/homebrew/bin/agy"),
                          URL(fileURLWithPath: "/usr/local/bin/agy")]
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    private var process: Process?

    func stop() {
        if let process, process.isRunning { process.terminate() }
    }

    func read() async throws -> [Quota] {
        guard let executable = Self.executable else { throw AntigravityFailure.missingCLI }
        let child = Process()
        let pipe = Pipe()
        let errors = Pipe()
        child.executableURL = executable
        child.arguments = ["-p", "/usage", "--output-format", "json", "--print-timeout", "30s"]
        child.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
        child.standardInput = FileHandle.nullDevice
        child.standardOutput = pipe
        child.standardError = errors
        do { try child.run() } catch { throw AntigravityFailure.unavailable }
        process = child
        var expired = false
        let timer = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 40_000_000_000)
            guard !Task.isCancelled, child.isRunning else { return }
            expired = true
            child.terminate()
        }
        defer {
            timer.cancel()
            if process === child { process = nil }
        }
        async let output = Self.drain(pipe.fileHandleForReading)
        async let diagnostic = Self.drain(errors.fileHandleForReading)
        let (data, errorData) = await (output, diagnostic)
        let status = await Task.detached(priority: .utility) {
            child.waitUntilExit()
            return child.terminationStatus
        }.value
        try Task.checkCancellation()
        if expired { throw AntigravityFailure.timeout }
        let quotas = AntigravityUsage.parse(data, at: .now)
        if status == 0, !quotas.isEmpty { return quotas }
        let lower = String(decoding: errorData + data, as: UTF8.self).lowercased()
        if ["sign in", "log in", "not logged", "unauthenticated", "authentication required"].contains(where: lower.contains) {
            throw AntigravityFailure.login
        }
        throw status == 0 ? AntigravityFailure.noReading : AntigravityFailure.unavailable
    }

    private nonisolated static func drain(_ handle: FileHandle) async -> Data {
        await Task.detached(priority: .utility) {
            var data = Data()
            defer { try? handle.close() }
            while let part = try? handle.read(upToCount: 16_384), !part.isEmpty {
                if data.count < 524_288 { data.append(part.prefix(524_288 - data.count)) }
            }
            return data
        }.value
    }
}
