import SwiftUI
import UIKit
import QuartzCore

/// The core renders through MoltenVK into a CAMetalLayer that it is handed from
/// here. Without this the emulator runs but nothing reaches the screen.
final class MetalHostUIView: UIView {
    override class var layerClass: AnyClass { CAMetalLayer.self }

    private var handedOver = false

    override func didMoveToWindow() {
        super.didMoveToWindow()
        handOver()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard let metal = layer as? CAMetalLayer else { return }
        metal.contentsScale = window?.screen.scale ?? 1
        metal.drawableSize = CGSize(width: bounds.width * metal.contentsScale,
                                    height: bounds.height * metal.contentsScale)
        handOver()
    }

    private func handOver() {
        guard !handedOver, window != nil, bounds.width > 0 else { return }
        handedOver = true
        RyujinxBridge.setNativeWindow(Unmanaged.passUnretained(layer).toOpaque())

        // Only now is it safe to boot: the core copies this layer once, early, and
        // does not retry if it is missing.
        DispatchQueue.main.async { EmulatorRunner.shared.layerReady() }
    }
}

struct MetalHostView: UIViewRepresentable {
    func makeUIView(context: Context) -> MetalHostUIView {
        let v = MetalHostUIView()
        v.backgroundColor = .black
        return v
    }
    func updateUIView(_ uiView: MetalHostUIView, context: Context) {}
}
