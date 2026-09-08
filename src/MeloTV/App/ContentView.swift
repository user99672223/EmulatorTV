import SwiftUI

struct ContentView: View {
    @StateObject private var library = GameLibrary()
    @StateObject private var runner = EmulatorRunner.shared
    @StateObject private var controllers = ControllerManager.shared

    @State private var selected: StoredFile?
    @State private var poolMiB: Int = 0     // 0 == leave the core's default alone

    private let poolChoices: [(String, Int)] = [
        ("Default (3285 MiB)", 0), ("1536 MiB", 1536), ("1280 MiB", 1280), ("1024 MiB", 1024),
    ]

    var body: some View {
        if runner.isRunning {
            MetalHostView()
                .ignoresSafeArea()
                .background(Color.black)
        } else {
            launcher
        }
    }

    private var launcher: some View {
        HStack(alignment: .top, spacing: 60) {
            VStack(alignment: .leading, spacing: 16) {
                Text("Files").font(.title2)

                if library.files.isEmpty {
                    Text("No files found in Library/Caches.\nUpload keys, firmware and a game, then Refresh.")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: 700, alignment: .leading)
                } else {
                    List(library.files, selection: $selected) { file in
                        Button {
                            if file.isGame { selected = file }
                        } label: {
                            HStack {
                                Text(file.name).lineLimit(1)
                                Spacer()
                                Text(file.sizeText).foregroundStyle(.secondary)
                                if file.isGame { Image(systemName: "gamecontroller") }
                            }
                        }
                    }
                    .frame(maxWidth: 900)
                }
            }

            VStack(alignment: .leading, spacing: 22) {
                Text("MeloTV").font(.largeTitle.bold())

                status

                Picker("Guest memory pool", selection: $poolMiB) {
                    ForEach(poolChoices, id: \.1) { Text($0.0).tag($0.1) }
                }
                .frame(maxWidth: 480)

                Button {
                    if let game = selected ?? library.games.first {
                        runner.start(game: game.url, applicationPoolMiB: poolMiB)
                    }
                } label: {
                    Label(startTitle, systemImage: "play.fill")
                }
                .disabled(!library.missing.isEmpty)

                Button {
                    library.refresh()
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }

                if let msg = runner.lastMessage {
                    Text(msg).foregroundStyle(.secondary).font(.footnote)
                }
            }
            .frame(maxWidth: 620, alignment: .leading)
        }
        .padding(60)
        .onAppear { library.refresh() }
    }

    private var startTitle: String {
        if let name = (selected ?? library.games.first)?.name { return "Start \(name)" }
        return "Start"
    }

    // Everything the emulator needs is reported here rather than letting the core
    // fail later with a less obvious message.
    @ViewBuilder private var status: some View {
        VStack(alignment: .leading, spacing: 8) {
            row("prod.keys", library.hasKeys)
            row("Firmware" + (library.firmwareVersion.map { " \($0)" } ?? ""), library.hasFirmware)
            row("Game", !library.games.isEmpty)
            row("Controller" + (controllers.connectedName.map { ": \($0)" } ?? ""),
                controllers.connectedName != nil)

            if !library.missing.isEmpty {
                Text("Missing: " + library.missing.joined(separator: ", "))
                    .font(.footnote)
                    .foregroundStyle(.orange)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func row(_ label: String, _ ok: Bool) -> some View {
        HStack(spacing: 10) {
            Image(systemName: ok ? "checkmark.circle.fill" : "xmark.circle")
                .foregroundStyle(ok ? .green : .orange)
            Text(label)
        }
    }
}
