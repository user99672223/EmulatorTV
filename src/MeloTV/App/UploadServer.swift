import Foundation
import Network
import os

/// A small HTTP server for getting keys, firmware and game images onto the device.
///
/// tvOS has no Files app, no document picker and no USB port, so an in-app server is
/// the only route -- the same one RetroArch and Provenance use. Everything lands in
/// Library/Caches, the only directory tvOS lets an app write to.
///
/// Uploads are raw PUT bodies streamed to disk a chunk at a time, never buffered.
/// That is forced by the constraints rather than chosen: the game image is 3.7 GB and
/// the process is killed past roughly 2 GB resident, so multipart form parsing, which
/// wants the whole body in hand, is not usable. The browser page uses fetch() with
/// PUT for the same reason, which also means there is no multipart parser at all.
final class UploadServer: ObservableObject {
    static let shared = UploadServer()

    @Published private(set) var isRunning = false
    @Published private(set) var address: String?
    @Published private(set) var lastEvent: String?

    private var listener: NWListener?
    private let port: UInt16 = 8080
    private let log = OSLog(subsystem: "com.melotv.app", category: "upload")

    /// Live sessions, held strongly for as long as the request runs.
    ///
    /// This is load-bearing. A session used to be a local in the accept handler, so it
    /// was deallocated the moment that function returned; every receive closure then
    /// hit its `guard let self else { return }` and did nothing at all. Connections
    /// were accepted and bodies transferred into the socket buffer, but nothing was
    /// ever read, written or answered -- and no log line ran either, which was the
    /// only visible symptom.
    private var sessions: [ObjectIdentifier: UploadSession] = [:]
    private let sessionLock = NSLock()

    private init() {}

    func start() {
        guard listener == nil else { return }

        do {
            let params = NWParameters.tcp
            params.allowLocalEndpointReuse = true

            let l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)

            l.newConnectionHandler = { [weak self] conn in
                self?.accept(conn)
            }

            l.stateUpdateHandler = { [weak self] state in
                guard let self else { return }
                switch state {
                case .ready:
                    let host = UploadServer.localIPv4() ?? "this-apple-tv"
                    let url = "http://\(host):\(self.port)"
                    self.report("Listening on \(url)")
                    DispatchQueue.main.async {
                        self.isRunning = true
                        self.address = url
                    }
                case .failed(let e):
                    self.report("Server failed: \(e)")
                    DispatchQueue.main.async { self.isRunning = false }
                case .cancelled:
                    DispatchQueue.main.async { self.isRunning = false }
                default:
                    break
                }
            }

            l.start(queue: .global(qos: .userInitiated))
            listener = l
        } catch {
            report("Could not start server: \(error)")
        }
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    private func accept(_ conn: NWConnection) {
        let session = UploadSession(connection: conn, server: self)

        sessionLock.lock()
        sessions[ObjectIdentifier(session)] = session
        sessionLock.unlock()

        report("Connection accepted")

        conn.stateUpdateHandler = { [weak self, weak session] state in
            guard let self, let session else { return }
            switch state {
            case .ready:
                session.readHeaders()
            case .failed(let e):
                self.report("Connection failed: \(e)")
                self.release(session)
            case .cancelled:
                self.release(session)
            default:
                break
            }
        }

        conn.start(queue: .global(qos: .userInitiated))
    }

    fileprivate func release(_ session: UploadSession) {
        sessionLock.lock()
        sessions.removeValue(forKey: ObjectIdentifier(session))
        sessionLock.unlock()
    }

    fileprivate func report(_ message: String) {
        os_log("%{public}s", log: log, type: .default, "[upload] " + message)
        DispatchQueue.main.async { self.lastEvent = message }
    }

    /// The Wi-Fi / Ethernet address, so the UI can show a URL that can be typed in.
    static func localIPv4() -> String? {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return nil }
        defer { freeifaddrs(head) }

        var fallback: String?
        for ptr in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let flags = Int32(ptr.pointee.ifa_flags)
            guard flags & IFF_UP != 0, flags & IFF_LOOPBACK == 0 else { continue }
            guard let addr = ptr.pointee.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) else { continue }

            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let ok = getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count),
                                 nil, 0, NI_NUMERICHOST)
            if ok == 0 {
                let name = String(cString: ptr.pointee.ifa_name)
                let ip = String(cString: host)
                if name.hasPrefix("en") { return ip }   // en0/en1 are Wi-Fi and Ethernet
                if fallback == nil { fallback = ip }
            }
        }
        return fallback
    }
}

/// One HTTP request. Header bytes accumulate until the blank line, then the body is
/// streamed to disk without ever being fully resident.
fileprivate final class UploadSession {
    private let connection: NWConnection
    private let server: UploadServer

    private var headerData = Data()
    private var fileHandle: FileHandle?
    private var destination: URL?
    private var contentLength = 0
    private var remaining = 0
    private var written = 0
    private var method = "?"
    private var path = "?"
    private var responded = false

    init(connection: NWConnection, server: UploadServer) {
        self.connection = connection
        self.server = server
    }

    func readHeaders() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, complete, error in
            guard let self else { return }

            if let error {
                self.server.report("Header read failed: \(error.localizedDescription)")
                self.finish()
                return
            }

            if let data, !data.isEmpty { self.headerData.append(data) }

            guard let split = self.headerData.range(of: Data("\r\n\r\n".utf8)) else {
                if complete {
                    self.server.report("Connection closed before headers completed")
                    self.finish()
                } else if self.headerData.count > 256 * 1024 {
                    self.respond(status: "431 Request Header Fields Too Large")
                } else {
                    self.readHeaders()
                }
                return
            }

            let head = String(decoding: self.headerData[..<split.lowerBound], as: UTF8.self)
            let body = Data(self.headerData[split.upperBound...])
            self.route(head: head, initialBody: body)
        }
    }

    private func route(head: String, initialBody: Data) {
        let lines = head.components(separatedBy: "\r\n")
        guard let request = lines.first else { respond(status: "400 Bad Request"); return }

        let parts = request.split(separator: " ")
        guard parts.count >= 2 else { respond(status: "400 Bad Request"); return }

        method = String(parts[0])
        let rawPath = String(parts[1])
        path = rawPath.removingPercentEncoding ?? rawPath

        server.report("\(method) \(path)")

        switch method {
        case "GET":
            respond(status: "200 OK", contentType: "text/html; charset=utf-8", body: Data(IndexPage.html().utf8))

        case "PUT", "POST":
            let lengthLine = lines.first { $0.lowercased().hasPrefix("content-length:") }
            contentLength = lengthLine
                .map { $0.dropFirst("content-length:".count).trimmingCharacters(in: .whitespaces) }
                .flatMap { Int($0) } ?? 0

            guard contentLength > 0 else {
                server.report("\(method) \(path) rejected: no Content-Length")
                respond(status: "411 Length Required")
                return
            }

            let name = String(path.drop(while: { $0 == "/" }))
            guard !name.isEmpty, !name.contains(".."), !name.contains("/") else {
                server.report("\(method) \(path) rejected: bad name")
                respond(status: "400 Bad Request")
                return
            }

            begin(name: name, initialBody: initialBody)

        default:
            respond(status: "405 Method Not Allowed")
        }
    }

    private func begin(name: String, initialBody: Data) {
        // prod.keys / title.keys belong in system/, where the emulator core looks for
        // them; everything else sits at the root of the data directory.
        let dir = name.hasSuffix(".keys") ? Paths.system : Paths.base

        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            server.report("Cannot create \(dir.path): \(error.localizedDescription)")
            respond(status: "500 Internal Server Error")
            return
        }

        let url = dir.appendingPathComponent(name)
        FileManager.default.createFile(atPath: url.path, contents: nil)

        guard let handle = try? FileHandle(forWritingTo: url) else {
            server.report("Cannot open \(url.path) for writing")
            respond(status: "500 Internal Server Error")
            return
        }

        destination = url
        fileHandle = handle
        remaining = contentLength
        written = 0

        server.report("Receiving \(name), \(Self.pretty(contentLength)) -> \(url.path)")

        guard writeChunk(initialBody) else { return }

        if remaining > 0 { readBody() } else { complete() }
    }

    private func writeChunk(_ data: Data) -> Bool {
        guard !data.isEmpty, let fileHandle else { return true }

        let slice = data.count > remaining ? data.prefix(remaining) : data
        do {
            try fileHandle.write(contentsOf: slice)
        } catch {
            server.report("Write failed after \(written) bytes: \(error.localizedDescription)")
            respond(status: "507 Insufficient Storage")
            return false
        }

        written += slice.count
        remaining -= slice.count
        return true
    }

    private func readBody() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { [weak self] data, _, complete, error in
            guard let self else { return }

            if let error {
                self.server.report("Upload interrupted after \(self.written) of \(self.contentLength) bytes: \(error.localizedDescription)")
                self.finish()
                return
            }

            if let data, !data.isEmpty, !self.writeChunk(data) { return }

            if self.remaining <= 0 {
                self.complete()
            } else if complete {
                self.server.report("Upload ended early: \(self.written) of \(self.contentLength) bytes, \(self.remaining) short")
                self.respond(status: "400 Bad Request",
                             body: Data("truncated at \(self.written) of \(self.contentLength) bytes\n".utf8))
            } else {
                self.readBody()
            }
        }
    }

    private func complete() {
        try? fileHandle?.close()
        fileHandle = nil

        let name = destination?.lastPathComponent ?? "file"
        // try? yields an optional dictionary, so it cannot be subscripted directly.
        let attrs = destination.flatMap { try? FileManager.default.attributesOfItem(atPath: $0.path) }
        let onDisk = attrs?[.size] as? Int

        server.report("Stored \(name): received \(written), on disk \(onDisk ?? -1) of \(contentLength) bytes")

        // The byte counts go back in the response so a multi-gigabyte upload can be
        // checked rather than assumed.
        var body = "stored \(name)\n"
        body += "received \(written) bytes\n"
        body += "on disk  \(onDisk ?? -1) bytes\n"
        body += "expected \(contentLength) bytes\n"

        let ok = written == contentLength && onDisk == contentLength
        respond(status: ok ? "200 OK" : "500 Internal Server Error", body: Data(body.utf8))
    }

    private func respond(status: String, contentType: String = "text/plain; charset=utf-8", body: Data = Data()) {
        guard !responded else { return }
        responded = true

        var header = "HTTP/1.1 " + status + "\r\n"
        header += "Content-Type: " + contentType + "\r\n"
        header += "Content-Length: \(body.count)\r\n"
        header += "Connection: close\r\n\r\n"

        server.report("\(method) \(path) -> \(status)")

        connection.send(content: Data(header.utf8) + body, completion: .contentProcessed { [weak self] error in
            if let error { self?.server.report("Send failed: \(error.localizedDescription)") }
            self?.finish()
        })
    }

    private func finish() {
        try? fileHandle?.close()
        fileHandle = nil
        connection.stateUpdateHandler = nil    // break the retain cycle with the server
        connection.cancel()
        server.release(self)
    }

    static func pretty(_ bytes: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
    }
}

private enum IndexPage {
    static func html() -> String {
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: Paths.base, includingPropertiesForKeys: [.fileSizeKey, .isRegularFileKey])) ?? []

        let rows = urls
            .filter { (try? $0.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .map { url -> String in
                let size = (try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                let pretty = ByteCountFormatter.string(fromByteCount: Int64(size), countStyle: .file)
                return "<tr><td>" + url.lastPathComponent + "</td><td>" + pretty + "</td></tr>"
            }
            .joined()

        let host = (UploadServer.localIPv4() ?? "apple-tv") + ":8080"
        let table = rows.isEmpty ? "<tr><td>(nothing yet)</td><td></td></tr>" : rows

        return page(table: table, host: host)
    }

    private static func page(table: String, host: String) -> String {
        var s = ""
        s += "<!doctype html><meta charset=utf-8><title>MeloTV</title>\n"
        s += "<style>body{font:16px system-ui;margin:40px;max-width:780px}"
        s += "table{border-collapse:collapse;width:100%}td{padding:6px 10px;border-bottom:1px solid #ddd}"
        s += "#drop{border:2px dashed #999;padding:36px;text-align:center;margin:20px 0;border-radius:8px}"
        s += "#log{white-space:pre-wrap;font:13px ui-monospace,monospace}</style>\n"
        s += "<h1>MeloTV</h1>\n"
        s += "<p>Send prod.keys, a firmware archive and the game here. Names ending in "
        s += "<code>.keys</code> are stored in <code>system/</code>; everything else at the root.</p>\n"
        s += "<div id=drop>Drop files here, or <input type=file id=f multiple></div>\n"
        s += "<div id=log></div>\n"
        s += "<h2>Already on the device</h2>\n<table>" + table + "</table>\n"
        s += "<h2>From a terminal (best for the 3.7 GB game)</h2>\n"
        s += "<pre>curl -T rl.nsp http://" + host + "/rl.nsp</pre>\n"
        s += "<script>\n"
        s += "const out=document.getElementById('log');\n"
        s += "const say=m=>{out.textContent+=m+String.fromCharCode(10);};\n"
        s += "async function send(files){\n"
        s += "  for(const file of files){\n"
        s += "    say('uploading '+file.name+' ...');\n"
        s += "    try{\n"
        s += "      const r=await fetch('/'+encodeURIComponent(file.name),{method:'PUT',body:file});\n"
        s += "      say(await r.text());\n"
        s += "    }catch(e){ say('FAILED '+file.name+' '+e); }\n"
        s += "  }\n"
        s += "  location.reload();\n"
        s += "}\n"
        s += "document.getElementById('f').onchange=e=>send(e.target.files);\n"
        s += "const d=document.getElementById('drop');\n"
        s += "d.ondragover=e=>e.preventDefault();\n"
        s += "d.ondrop=e=>{e.preventDefault();send(e.dataTransfer.files);};\n"
        s += "</script>\n"
        return s
    }
}
