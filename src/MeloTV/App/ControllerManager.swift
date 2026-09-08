import Foundation
import GameController

/// Feeds a physical controller straight into the core through its native gamepad
/// API. There is no touch input on tvOS and no on-screen pad, so this is the only
/// way anything reaches the guest.
final class ControllerManager: ObservableObject {
    static let shared = ControllerManager()

    @Published private(set) var connectedName: String?

    private let lock = NSLock()
    private var token: UnsafeMutableRawPointer?
    private var current: GCController?

    private init() {}

    func begin() {
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] note in
                self?.attach(note.object as? GCController)
            }
        NotificationCenter.default.addObserver(
            forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] _ in
                self?.detach()
            }
        attach(GCController.controllers().first)
    }

    private func attach(_ controller: GCController?) {
        guard let controller, current == nil else { return }
        guard let pad = controller.extendedGamepad else { return }   // ignore the bare Siri Remote

        // Any stable non-null pointer works; the core uses it purely as identity.
        let id = UnsafeMutableRawPointer(bitPattern: UInt(1))
        RyujinxBridge.attachGamepad(id, controller.vendorName ?? "Controller")

        lock.lock(); token = id; lock.unlock()
        current = controller
        connectedName = controller.vendorName

        pad.valueChangedHandler = { [weak self] pad, _ in
            self?.push(pad)
        }
    }

    private func detach() {
        lock.lock()
        if let token { RyujinxBridge.detachGamepad(token) }
        token = nil
        lock.unlock()
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

    private func set(_ id: UnsafeMutableRawPointer, _ button: B, _ pressed: Bool) {
        RyujinxBridge.setGamepadButtonState(id, buttonId: button.rawValue, pressed: pressed)
    }

    private func push(_ pad: GCExtendedGamepad) {
        lock.lock(); let id = token; lock.unlock()
        guard let token = id else { return }

        // GameController names buttons by position: buttonA is the bottom face
        // button. On a Switch pad the bottom button is B and the right one is A,
        // so the two pairs are swapped here rather than in the guest.
        set(token, .b, pad.buttonA.isPressed)
        set(token, .a, pad.buttonB.isPressed)
        set(token, .y, pad.buttonX.isPressed)
        set(token, .x, pad.buttonY.isPressed)

        set(token, .leftShoulder, pad.leftShoulder.isPressed)
        set(token, .rightShoulder, pad.rightShoulder.isPressed)
        set(token, .leftTrigger, pad.leftTrigger.isPressed)
        set(token, .rightTrigger, pad.rightTrigger.isPressed)

        set(token, .dPadUp, pad.dpad.up.isPressed)
        set(token, .dPadDown, pad.dpad.down.isPressed)
        set(token, .dPadLeft, pad.dpad.left.isPressed)
        set(token, .dPadRight, pad.dpad.right.isPressed)

        set(token, .start, pad.buttonMenu.isPressed)
        if let options = pad.buttonOptions { set(token, .back, options.isPressed) }
        if let home = pad.buttonHome { set(token, .guide, home.isPressed) }
        if let l3 = pad.leftThumbstickButton { set(token, .leftStick, l3.isPressed) }
        if let r3 = pad.rightThumbstickButton { set(token, .rightStick, r3.isPressed) }

        // 1 == left stick, 2 == right stick.
        RyujinxBridge.setGamepadStickAxis(token, stickId: 1,
                                          x: pad.leftThumbstick.xAxis.value,
                                          y: pad.leftThumbstick.yAxis.value)
        RyujinxBridge.setGamepadStickAxis(token, stickId: 2,
                                          x: pad.rightThumbstick.xAxis.value,
                                          y: pad.rightThumbstick.yAxis.value)
    }
}
