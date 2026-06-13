namespace DevKitRelay;

internal sealed record GamepadState(
    string Provider,
    int ControllerIndex,
    bool IsConnected,
    ulong Sequence,
    long TimestampUnixMilliseconds,
    ushort Buttons,
    double LeftThumbX,
    double LeftThumbY,
    double RightThumbX,
    double RightThumbY,
    double LeftTrigger,
    double RightTrigger)
{
    public bool HasSameInput(GamepadState? other) =>
        other is not null &&
        Provider == other.Provider &&
        ControllerIndex == other.ControllerIndex &&
        IsConnected == other.IsConnected &&
        Buttons == other.Buttons &&
        LeftThumbX.Equals(other.LeftThumbX) &&
        LeftThumbY.Equals(other.LeftThumbY) &&
        RightThumbX.Equals(other.RightThumbX) &&
        RightThumbY.Equals(other.RightThumbY) &&
        LeftTrigger.Equals(other.LeftTrigger) &&
        RightTrigger.Equals(other.RightTrigger);
}
