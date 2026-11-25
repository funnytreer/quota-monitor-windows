import CryptoKit
import Foundation

enum CodexFailure: Error {
    case missingCLI, connection, loginRequired, noReading, invalidResponse
    case rateLimited(TimeInterval?)
    case remote

    var message: String {
        switch self {
        case .missingCLI: "找不到 Codex CLI。请安装官方 CLI 或 ChatGPT Mac 客户端。"
        case .connection: "无法连接本地 Codex。请确认 Codex 能正常运行后重试。"
        case .loginRequired: "请在官方 Codex 中使用 ChatGPT 账号登录后重新连接。"
        case .noReading: "当前账号暂未返回可显示的 Codex 额度。"
        case .invalidResponse: "Codex 返回了无法识别的额度格式。"
        case .rateLimited: "Codex 暂时限制查询频率。"
        case .remote: "Codex 本次查询失败。"
        }
    }
}

@MainActor
final class CodexClient {
    struct Response {
        let account: String
        let windows: CodexWindows
    }

    private struct Pending {
        let continuation: CheckedContinuation<Data, Error>
        let timeout: Task<Void, Never>
    }
    private var pending: [Int: Pending] = [:]
    private var nextID = 0
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var buffer = Data()
    private var connectionID: UUID?
    var didUpdate: ((CodexWindows, Date) -> Void)?
    var accountDidChange: (() -> Void)?

    static var executable: URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let resourcePaths = [
            "Contents/Resources/codex-cli/bin/codex",
            "Contents/Resources/codex"
        ]
        let apps = ["/Applications/ChatGPT.app", "/Applications/Codex.app",
                    home.appendingPathComponent("Applications/ChatGPT.app").path,
                    home.appendingPathComponent("Applications/Codex.app").path]
        let paths = apps.flatMap { app in resourcePaths.map { app + "/" + $0 } } + [
            "/opt/homebrew/bin/codex", "/usr/local/bin/codex",
            home.appendingPathComponent(".local/bin/codex").path,
            home.appendingPathComponent(".codex/bin/codex").path
        ]
        return paths.first(where: { FileManager.default.isExecutableFile(atPath: $0) }).map { URL(fileURLWithPath: $0) }
    }

    func refresh(accountObserved: (String) -> Void) async throws -> Response {
        if process == nil { try await start() }
        let first = try await readAccount()
        accountObserved(first)
        let data = try await request("account/rateLimits/read")
        let second = try await readAccount()
        if second != first {
            accountObserved(second)
            throw CodexFailure.connection
        }
        guard let windows = try? JSONDecoder().decode(CodexWindows.self, from: data), windows.hasPayload else {
            throw CodexFailure.invalidResponse
        }
        return Response(account: second, windows: windows)
    }

    func readAccount() async throws -> String {
        let data = try await request("account/read", params: ["refreshToken": false])
        struct AccountResponse: Decodable {
            struct Account: Decodable {
                let type: String
                let email: String?
                let accountId: String?
                let planType: String?
            }
            let account: Account?
        }
        guard let account = try JSONDecoder().decode(AccountResponse.self, from: data).account,
              account.type == "chatgpt",
              let identifier = account.accountId ?? account.email?.lowercased() else {
            throw CodexFailure.loginRequired
        }
        return SHA256.hash(data: Data(("QuotaMonitor:Codex:" + identifier).utf8))
            .prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    private func start() async throws {
        guard let executable = Self.executable else { throw CodexFailure.missingCLI }
        let state = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                                appropriateFor: nil, create: true)
            .appendingPathComponent("QuotaMonitor/Codex", isDirectory: true)
        try FileManager.default.createDirectory(at: state, withIntermediateDirectories: true)
        let stateLiteral = String(decoding: try JSONEncoder().encode(state.path), as: UTF8.self)
            .replacingOccurrences(of: "\\/", with: "/")
        let process = Process()
        let stdin = Pipe()
        let stdout = Pipe()
        process.executableURL = executable
        process.currentDirectoryURL = state
        process.arguments = ["app-server", "-c", "sqlite_home=\(stateLiteral)", "-c", "log_dir=\(stateLiteral)"]
        process.standardInput = stdin
        process.standardOutput = stdout
        process.standardError = FileHandle.nullDevice
        let id = UUID()
        connectionID = id
        let receiver = stdout.fileHandleForReading
        receiver.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            DispatchQueue.main.async { [weak self] in self?.receive(data, connection: id) }
        }
        process.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async { [weak self] in
                guard let self, self.connectionID == id else { return }
                self.close()
            }
        }
        self.process = process
        input = stdin.fileHandleForWriting
        output = receiver
        if let input { _ = fcntl(input.fileDescriptor, F_SETNOSIGPIPE, 1) }
        do {
            try process.run()
            _ = try await request("initialize", params: [
                "clientInfo": ["name": "quota_monitor", "title": "QuotaMonitor", "version": "0.1.0"]
            ])
            try write(["method": "initialized"])
        } catch {
            close()
            throw CodexFailure.connection
        }
    }

    private func request(_ method: String, params: [String: Any]? = nil) async throws -> Data {
        guard input != nil else { throw CodexFailure.connection }
        nextID += 1
        let id = nextID
        return try await withCheckedThrowingContinuation { continuation in
            let timeout = Task { [weak self] in
                try? await Task.sleep(for: .seconds(15))
                guard !Task.isCancelled, let self, self.pending[id] != nil else { return }
                self.close()
            }
            pending[id] = Pending(continuation: continuation, timeout: timeout)
            var message: [String: Any] = ["id": id, "method": method]
            message["params"] = params
            do { try write(message) } catch { close() }
        }
    }

    private func write(_ value: [String: Any]) throws {
        guard let input else { throw CodexFailure.connection }
        var data = try JSONSerialization.data(withJSONObject: value)
        data.append(10)
        try input.write(contentsOf: data)
    }

    private func receive(_ data: Data, connection: UUID) {
        guard connectionID == connection else { return }
        guard !data.isEmpty else { close(); return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<newline])
            buffer.removeSubrange(...newline)
            handle(line)
        }
        if buffer.count > 4 * 1024 * 1024 { close() }
    }

    private func handle(_ line: Data) {
        guard let message = (try? JSONSerialization.jsonObject(with: line)) as? [String: Any] else { return }
        if let method = message["method"] as? String {
            if let id = message["id"] {
                try? write(["id": id, "error": ["code": -32601, "message": "Unsupported request"]])
            } else if method == "account/updated" {
                accountDidChange?()
            } else if method == "account/rateLimits/updated", let params = message["params"],
                      let data = try? JSONSerialization.data(withJSONObject: params),
                      let windows = try? JSONDecoder().decode(CodexWindows.self, from: data) {
                didUpdate?(windows, .now)
            }
            return
        }
        guard let id = message["id"] as? Int, let request = pending.removeValue(forKey: id) else { return }
        request.timeout.cancel()
        if let error = message["error"] as? [String: Any] {
            request.continuation.resume(throwing: Self.remoteError(error))
        } else if let result = message["result"], let data = try? JSONSerialization.data(withJSONObject: result) {
            request.continuation.resume(returning: data)
        } else {
            request.continuation.resume(throwing: CodexFailure.invalidResponse)
        }
    }

    private static func remoteError(_ error: [String: Any]) -> CodexFailure {
        let details = error["data"] as? [String: Any]
        let message = (error["message"] as? String ?? "").lowercased()
        let code = error["code"] as? Int
        let status = details?["httpStatusCode"] as? Int ?? details?["status"] as? Int
        if code == 401 || status == 401 || message.contains("not logged in") ||
            message.contains("unauthorized") || message.contains("authentication required") {
            return .loginRequired
        }
        if code == 429 || status == 429 || message.contains("429") || message.contains("too many requests") {
            let header = details?["retry-after"] as? String ?? details?["retryAfter"] as? String
            let seconds = details?["retryAfterSeconds"] as? Double ?? header.flatMap(Double.init)
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
            let dateDelay = header.flatMap { formatter.date(from: $0)?.timeIntervalSinceNow }
            return .rateLimited(max(1, seconds ?? dateDelay ?? 60))
        }
        return .remote
    }

    func close() {
        connectionID = nil
        output?.readabilityHandler = nil
        try? output?.close()
        try? input?.close()
        input = nil
        output = nil
        if process?.isRunning == true { process?.terminate() }
        process = nil
        buffer.removeAll()
        let requests = pending
        pending.removeAll()
        for request in requests.values {
            request.timeout.cancel()
            request.continuation.resume(throwing: CodexFailure.connection)
        }
    }
}
