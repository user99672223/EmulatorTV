import SwiftUI

struct ContentView: View {
    @StateObject private var library = GameLibrary()
    @ObservedObject private var runner = EmulatorRunner.shared
    @ObservedObject private var controllers = ControllerManager.shared
    @ObservedObject private var uploads = UploadServer.shared

    @State private var selected: StoredFile?
    // 1536 by default rather than the stock 3285: the pools have to fit inside the
    // reduced DRAM, and the measured desktop peak for this title was about 1200 MiB.
    @State private var poolMiB: Int = 1750
    @State private var asBlockMiB: Int = 64
    @State private var note: String?

    // 1750 is the default: it is the value verified all the way to Rocket League's
    // main menu. 1850 also boots and leaves the game more room, but trims the
    // service pool to 129 MiB.
    //
    // Do not raise this past 1850 on a 2048 MiB DRAM. The pools are carved from the
    // top down and the service pool takes what is left, so a larger application pool
    // pushes it to its 64 MiB floor -- at which point the process dies loading the
    // main module, before the game runs and without printing anything. 1915 does
    // exactly that.
    // Partition allocator block size. 256 is the old default and the one that left
    // 2078 MiB of the guest's ~6 GiB hole reserved and unmapped. Selectable so the
    // useful value can be found on the device in minutes rather than one build each.
    private let asBlockChoices: [(String, Int)] = [
        ("AS block 64 MiB", 64), ("AS block 32 MiB", 32),
        ("AS block 16 MiB", 16), ("AS block 256 MiB (old)", 256),
    ]

    private let poolChoices: [(String, Int)] = [
        ("1750 MiB", 1750), ("1850 MiB", 1850), ("1536 MiB", 1536),
        ("1280 MiB", 1280), ("Stock (3285 MiB)", 0),
    ]

    var body: some View {
        if runner.showsOutput {
            // Mounted while preparing too, so the CAMetalLayer exists before the core
            // looks for it.
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
                    List(library.files) { file in
                        Button {
                            if file.isGame { selected = file }
                        } label: {
                            HStack {
                                Image(systemName: selected?.id == file.id
                                      ? "largecircle.fill.circle" : "circle")
                                    .opacity(file.isGame ? 1 : 0)
                                Text(file.name).lineLimit(1)
                                Spacer()
                                Text(file.sizeText).foregroundStyle(.secondary)
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

                Picker("Address space block", selection: $asBlockMiB) {
                    ForEach(asBlockChoices, id: \.1) { Text($0.0).tag($0.1) }
                }
                .frame(maxWidth: 480)

                // Deliberately never disabled: a disabled button cannot take focus on
                // tvOS, so gating it would strand the remote with nothing to select.
                // It reports what is missing instead.
                Button {
                    guard library.blocking.isEmpty else {
                        note = "Cannot start - missing " + library.blocking.joined(separator: ", ")
                        return
                    }
                    if let game = selected ?? library.games.first {
                        runner.start(game: game.url, applicationPoolMiB: poolMiB, asBlockAlignMiB: asBlockMiB)
                    }
                } label: {
                    Label(startTitle, systemImage: "play.fill")
                }

                if let firmware = library.installableFirmware.first {
                    Button {
                        note = library.installFirmware(firmware)
                    } label: {
                        Label("Install firmware from \(firmware.name)", systemImage: "square.and.arrow.down")
                    }
                }

                Button {
                    library.refresh()
                    note = nil
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }

                if let note {
                    Text(note).font(.footnote).foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Divider()

                VStack(alignment: .leading, spacing: 6) {
                    Text("Upload").font(.headline)
                    if let address = uploads.address {
                        Text(address).font(.title3.monospaced())
                        Text("Open that in a browser on your PC")
                            .font(.footnote).foregroundStyle(.secondary)
                        Text("curl -T rl.nsp " + address + "/rl.nsp")
                            .font(.footnote.monospaced()).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    } else {
                        Text(uploads.isRunning ? "starting..." : "server not running")
                            .foregroundStyle(.secondary)
                    }
                    if let event = uploads.lastEvent {
                        Text(event).font(.footnote).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                if let msg = runner.lastMessage {
                    Text(msg).foregroundStyle(.secondary).font(.footnote)
                }
            }
            .frame(maxWidth: 620, alignment: .leading)
        }
        .padding(60)
        .onAppear {
            library.refresh()
            uploads.start()
        }
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

            ForEach(library.blocking + library.warnings, id: \.self) { item in
                Text(item)
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
