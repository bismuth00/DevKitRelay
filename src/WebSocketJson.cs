using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DevKitRelay;

internal static class WebSocketJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static async Task SendAsync<T>(WebSocket socket, T message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(buffer, WebSocketMessageType.Text, true, cancellationToken);
    }

    public static async Task<T?> ReceiveAsync<T>(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return default;
                }

                stream.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(stream.ToArray(), SerializerOptions);
    }
}
