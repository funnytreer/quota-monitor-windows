using System.Buffers.Binary;
using System.IO;
using System.Text;
using Xunit;

namespace QuotaMonitor.Tests;

public class GeminiBridgeTests
{
    [Fact]
    public async Task BrowserMessagesUpdateWizardStateOncePerTransition()
    {
        var bridge = new GeminiConnection();
        var changes = new List<GeminiBridgeState>();
        bridge.StateChanged += changes.Add;
        Assert.Equal(GeminiBridgeState.WaitingForChrome, bridge.State);
        async Task<GeminiReading> Send(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var frame = new byte[bytes.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
            bytes.CopyTo(frame, 4);
            using var stream = new MemoryStream(frame);
            return await bridge.ReceiveAsync(stream, default);
        }
        await Send("""{"type":"permission"}""");
        await Send("""{"type":"permission"}""");
        await Send("""{"type":"signIn"}""");
        var reading = await Send("""{"type":"usage","current":{"used":23},"weekly":{"used":9,"reset":"周四"}}""");
        Assert.Equal((77, 91, "周四"), (reading.Current.Remaining, reading.Weekly?.Remaining, reading.Weekly?.ResetText));
        await Send("""{"type":"unavailable"}""");
        Assert.Equal(new[] { GeminiBridgeState.NeedsPermission, GeminiBridgeState.NeedsSignIn, GeminiBridgeState.Connected,
            GeminiBridgeState.ReadFailed }, changes);
    }
}
