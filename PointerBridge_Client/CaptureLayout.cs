namespace PointerBridge_Client;

internal readonly record struct CaptureLayout(double Left, double Top, double Width, double Height)
{
    public static CaptureLayout Fit(double width, double height, double aspectRatio)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height)
            || aspectRatio <= 0 || !double.IsFinite(aspectRatio))
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));

        double captureWidth = Math.Min(width, height * aspectRatio);
        double captureHeight = captureWidth / aspectRatio;
        return new((width - captureWidth) / 2, (height - captureHeight) / 2,
            captureWidth, captureHeight);
    }
}
