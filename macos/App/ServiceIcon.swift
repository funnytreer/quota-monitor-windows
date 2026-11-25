import SwiftUI

struct ServiceIcon: View {
    @Environment(\.colorScheme) private var colorScheme

    let service: Service
    var group: AntigravityGroup? = nil
    let size: CGFloat

    private var assetName: String? {
        switch service {
        case .codex: "CodexMark"
        case .grok: "GrokMark"
        case .antigravity: group == .gemini ? "GeminiMark" : "AntigravityMark"
        case .legacyGeminiWeb: nil
        }
    }

    var body: some View {
        if let assetName {
            Image(assetName, bundle: .main)
                .renderingMode(.template)
                .resizable()
                .scaledToFit()
                .foregroundStyle(colorScheme == .dark ? Color.white : Color.black)
                .frame(width: size, height: size)
                .accessibilityHidden(true)
        }
    }
}
