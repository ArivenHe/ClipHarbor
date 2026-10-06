import SwiftUI

// Editing focus makes controls reachable with Tab even when system-wide
// keyboard navigation is off. Activation stays local to the focused control.
struct KeyboardButton<Label: View>: View {
    @Environment(\.isEnabled) private var isEnabled
    var role: ButtonRole?
    var action: () -> Void
    var label: Label

    init(role: ButtonRole? = nil, action: @escaping () -> Void, @ViewBuilder label: () -> Label) {
        self.role = role; self.action = action; self.label = label()
    }
    init(_ title: String, role: ButtonRole? = nil, action: @escaping () -> Void) where Label == Text {
        self.init(role: role, action: action) { Text(title) }
    }
    var body: some View {
        Button(role: role, action: action) { label }
            .focusable(interactions: .edit)
            .onKeyPress(keys: [.return, .space], phases: .down) { press in
                guard isEnabled, press.modifiers.intersection([.command, .option, .control]).isEmpty else { return .ignored }
                action()
                return .handled
            }
    }
}

struct KeyboardToggle<Label: View>: View {
    @Environment(\.isEnabled) private var isEnabled
    @Binding var isOn: Bool
    var label: Label
    init(isOn: Binding<Bool>, @ViewBuilder label: () -> Label) {
        _isOn = isOn; self.label = label()
    }
    init(_ title: String, isOn: Binding<Bool>) where Label == Text {
        self.init(isOn: isOn) { Text(title) }
    }
    var body: some View {
        Toggle(isOn: $isOn) { label }
            .focusable(interactions: .edit)
            .onKeyPress(keys: [.return, .space], phases: .down) { press in
                guard isEnabled, press.modifiers.intersection([.command, .option, .control]).isEmpty else { return .ignored }
                isOn.toggle()
                return .handled
            }
    }
}

struct KeyboardStepper: View {
    @Environment(\.isEnabled) private var isEnabled
    var title: String
    @Binding var value: Int
    var range: ClosedRange<Int>
    var step: Int
    init(_ title: String, value: Binding<Int>, in range: ClosedRange<Int>, step: Int = 1) {
        self.title = title; _value = value; self.range = range; self.step = step
    }
    var body: some View {
        Stepper(title, value: $value, in: range, step: step)
            .focusable(interactions: .edit)
            .onKeyPress(keys: [.leftArrow, .rightArrow, .upArrow, .downArrow]) { press in
                guard isEnabled, press.modifiers.intersection([.command, .option, .control]).isEmpty else { return .ignored }
                let direction = press.key == .upArrow || press.key == .rightArrow ? 1 : -1
                value = min(max(value + direction * step, range.lowerBound), range.upperBound)
                return .handled
            }
    }
}

extension View {
    func keyboardPicker(selection: Binding<String>, values: [String]) -> some View {
        focusable(interactions: .edit)
            .onKeyPress(keys: [.leftArrow, .rightArrow, .upArrow, .downArrow]) { press in
                guard press.modifiers.intersection([.command, .option, .control]).isEmpty,
                      let index = values.firstIndex(of: selection.wrappedValue), !values.isEmpty else { return .ignored }
                let direction = press.key == .upArrow || press.key == .leftArrow ? -1 : 1
                selection.wrappedValue = values[min(max(index + direction, 0), values.count - 1)]
                return .handled
            }
    }
}
