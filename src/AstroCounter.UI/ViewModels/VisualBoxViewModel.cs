namespace AstroCounter.UI.ViewModels;

using AstroCounter.Core.Models;

public record VisualBoxViewModel(
    int Id,
    double Left,
    double Top,
    double Width,
    double Height,
    string ColorHex,
    string LabelText,
    float Confidence,
    ConfidenceTier Tier
)
{
    public string TooltipText => $"{LabelText}\nKonum: ({Left:F0}, {Top:F0})\nBoyut: {Width:F0}x{Height:F0}";
}
