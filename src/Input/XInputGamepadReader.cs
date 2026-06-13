using System.Runtime.InteropServices;

namespace DevKitRelay;

internal sealed class XInputGamepadReader : IGamepadReader
{
    private const int ErrorSuccess = 0;
    private const int MaxControllers = 4;
    private const short LeftThumbDeadZone = 7849;
    private const short RightThumbDeadZone = 8689;
    private const byte TriggerThreshold = 30;

    public string ProviderName => "XInput";

    public GamepadState Read()
    {
        for (var controllerIndex = 0; controllerIndex < MaxControllers; controllerIndex++)
        {
            var result = XInputGetState(controllerIndex, out var state);
            if (result != ErrorSuccess)
            {
                continue;
            }

            return new GamepadState(
                ProviderName,
                controllerIndex,
                IsConnected: true,
                Sequence: 0,
                TimestampUnixMilliseconds: 0,
                state.Gamepad.Buttons,
                NormalizeThumb(state.Gamepad.ThumbLX, LeftThumbDeadZone),
                NormalizeThumb(state.Gamepad.ThumbLY, LeftThumbDeadZone),
                NormalizeThumb(state.Gamepad.ThumbRX, RightThumbDeadZone),
                NormalizeThumb(state.Gamepad.ThumbRY, RightThumbDeadZone),
                NormalizeTrigger(state.Gamepad.LeftTrigger),
                NormalizeTrigger(state.Gamepad.RightTrigger));
        }

        return new GamepadState(
            ProviderName,
            ControllerIndex: -1,
            IsConnected: false,
            Sequence: 0,
            TimestampUnixMilliseconds: 0,
            Buttons: 0,
            LeftThumbX: 0,
            LeftThumbY: 0,
            RightThumbX: 0,
            RightThumbY: 0,
            LeftTrigger: 0,
            RightTrigger: 0);
    }

    public void Dispose()
    {
    }

    private static double NormalizeThumb(short value, short deadZone)
    {
        if (Math.Abs(value) < deadZone)
        {
            return 0;
        }

        return value < 0
            ? Math.Max(-1, value / 32768.0)
            : Math.Min(1, value / 32767.0);
    }

    private static double NormalizeTrigger(byte value)
    {
        if (value <= TriggerThreshold)
        {
            return 0;
        }

        return (value - TriggerThreshold) / (255.0 - TriggerThreshold);
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XInputState pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }
}
