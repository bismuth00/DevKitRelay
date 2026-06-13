namespace DevKitRelay;

internal interface IGamepadReader : IDisposable
{
    string ProviderName { get; }

    GamepadState Read();
}
