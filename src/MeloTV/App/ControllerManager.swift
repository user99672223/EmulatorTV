import Foundation
import GameController

/// Feeds a physical controller straight into the core through its native gamepad
/// API. There is no touch input on tvOS and no on-screen pad, so this is the only
/// way anything reaches the guest.
@MainActor
final class ControllerManager: ObservableObject {
    static let shared = ControllerManager()

    @Published private(set) var connectedName: String?

    private var token: UnsafeMutableRawPointer?
    private var current: GCController?

    private init() {}

    func begin() {
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] note in
                Task { @MainActor in self?.attach(note.object as? GCController) }
            }
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] _ in
                Task { @MainActor in self?.detach() }
            }
        attach(GCController.controllers().first)
    }

    private func attach(_ controller: GCController?) {
        guard let controller, current == nil else { return }
        guard let pad = controller.extendedGamepad else { return }   // ignore the bare Siri Remote

        // Any stable non-null pointer works; the core uses it purely as identity.
        let id = UnsafeMutableRawPointer(bitPattern: UInt(1))
        RyujinxBridge.attachGamepad(id, controller.vendorName ?? "Controller")

        token = id
        current = controller
        connectedName = controller.vendorName

        pad.valueChangedHandler = { [weak self] pad, _ in
            self?.push(pad)
        }
    }

    private func detach() {
        if let token { RyujinxBridge.detachGamepad(token) }
        token = nil
        current = nil
        connectedName = nil
    }

    // Index order matches the core's ButtonMapping table exactly.
    private enum B: Int {
        case a = 0, b = 1, x = 2, y = 3, back = 4, guide = 5, start = 6
        case leftStick = 7, rightStick = 8, leftShoulder = 9, rightShoulder = 10
        case dPadUp = 11, dPadDown = 12, dPadLeft = 13, dPadRight = 14
        case leftTrigger = 15, rightTrigger = 16
    }

    private func set(_ button: B, _ pressed: Bool) {
        RyujinxBridge.setGamepadButtonState(token, buttonId: button.rawValue, pressed: pressed)
    }

    private func push(_ pad: GCExtendedGamepad) {
        guard token != nil else { return }

        // GameController names buttons by position: buttonA is the bottom face
        // button. On a Switch pad the bottom button is B and the right one is A,
        // so the two pairs are swapped here rather than in the guest.
        set(.b, pad.buttonA.isPressed)
        set(.a, pad.buttonB.isPressed)
        set(.y, pad.buttonX.isPressed)
        set(.x, pad.buttonY.isPressed)

        set(.leftShoulder, pad.leftShoulder.isPressed)
        set(.rightShoulder, pad.rightShoulder.isPressed)
        set(.leftTrigger, pad.leftTrigger.isPressed)
        set(.rightTrigger, pad.rightTrigger.isPressed)

        set(.dPadUp, pad.dpad.up.isPressed)
        set(.dPadDown, pad.dpad.down.isPressed)
        set(.dPadLeft, pad.dpad.left.isPressed)
        set(.dPadRight, pad.dpad.right.isPressed)

        set(.start, pad.buttonMenu.isPressed)
        if let options = pad.buttonOptions { set(.back, options.isPressed) }
        if let home = pad.buttonHome { set(.guide, home.isPressed) }
        if let l3 = pad.leftThumbstickButton { set(.leftStick, l3.isPressed) }
        if let r3 = pad.rightThumbstickButton { set(.rightStick, r3.isPressed) }

        // 1 == left stick, 2 == right stick.
        RyujinxBridge.setGamepadStickAxis(token, stickId: 1,
                                          x: pad.leftThumbstick.xAxis.value,
                                          y: pad.leftThumbstick.yAxis.value)
        RyujinxBridge.setGamepadStickAxis(token, stickId: 2,
                                          x: pad.rightThumbstick.xAxis.value,
                                          y: pad.rightThumbstick.yAxis.value)
    }
}
