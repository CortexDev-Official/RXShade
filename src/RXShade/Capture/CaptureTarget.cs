using RXShade.Interop;
using Windows.Graphics.Capture;

namespace RXShade.Capture;

/// <summary>
/// A capturable Roblox game window. Holds only a handle plus public window
/// metadata (title / class / owning process name) - nothing is read from the
/// target process itself.
/// </summary>
public sealed class CaptureTarget
{
    public required IntPtr Handle { get; init; }
    public required string Title { get; init; }
    public string ClassName { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;

    public string DisplayName => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  —  {ProcessName}";

    public bool IsAlive => NativeMethods.IsWindow(Handle);

    public GraphicsCaptureItem CreateCaptureItem() => WinRtInterop.CreateItemForWindow(Handle);

    public override string ToString() => DisplayName;
}
