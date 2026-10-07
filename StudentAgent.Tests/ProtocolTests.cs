using System.Text;
using ClassroomControl.StudentAgent.Communication.Messages;
using ClassroomControl.StudentAgent.Communication.Protocol;
using ClassroomControl.StudentAgent.Communication.Security;
using ClassroomControl.StudentAgent.Communication.Tcp;
using ClassroomControl.StudentAgent.Models;
using Xunit;

namespace ClassroomControl.StudentAgent.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Wire_message_roundtrips()
    {
        var original = new WireMessage
        {
            ProtocolVersion = "1.0", Type = MessageTypes.Heartbeat, SessionId = "s1", MessageId = "m1",
            Sequence = 42, Timestamp = 1_700_000_000_000, Payload = "{\"a\":1}", Signature = "sig",
        };
        var json = MessageSerializer.Serialize(original);
        var copy = MessageSerializer.DeserializeWire(json);
        Assert.Equal(original.Type, copy.Type);
        Assert.Equal(42, copy.Sequence);
        Assert.Equal("{\"a\":1}", copy.Payload);
        Assert.Contains("\"type\":\"HEARTBEAT\"", json, StringComparison.Ordinal); // camelCase property names
    }

    [Fact]
    public void Enums_are_serialized_as_text()
    {
        var json = MessageSerializer.Serialize(new RegistrationUpdateMessage(RegistrationState.Approved, null, new AgentPolicy(true)));
        Assert.Contains("\"registration\":\"Approved\"", json, StringComparison.Ordinal);
        Assert.Equal(RegistrationState.Approved, MessageSerializer.Deserialize<RegistrationUpdateMessage>(json).Registration);
    }

    [Fact]
    public void Command_result_roundtrips_with_payload()
    {
        var result = new CommandResult("123", "abc", CommandStatus.Success, DateTimeOffset.UtcNow, null, "Status retrieved",
            System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true }));
        var copy = MessageSerializer.Deserialize<CommandResult>(MessageSerializer.Serialize(result));
        Assert.Equal("123", copy.CommandId);
        Assert.Equal(CommandStatus.Success, copy.Status);
        Assert.True(copy.Payload!.Value.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"HELLO\"}")]
    public void Invalid_wire_messages_are_rejected_with_a_protocol_error(string json)
    {
        var ex = Assert.Throws<ProtocolException>(() => MessageSerializer.DeserializeWire(json));
        Assert.Equal(ErrorCodes.InvalidMessage, ex.ErrorCode);
    }

    [Fact]
    public async Task Frames_roundtrip_over_a_stream()
    {
        var stream = new MemoryStream();
        var framed = new FramedConnection(stream);
        await framed.WriteFrameAsync("salom dunyo 🌍", default);
        await framed.WriteFrameAsync("{\"x\":1}", default);
        stream.Position = 0;
        Assert.Equal("salom dunyo 🌍", await framed.ReadFrameAsync(default));
        Assert.Equal("{\"x\":1}", await framed.ReadFrameAsync(default));
        Assert.Null(await framed.ReadFrameAsync(default)); // clean end of stream
    }

    [Fact]
    public async Task Oversized_incoming_frame_is_rejected_before_allocation()
    {
        var stream = new MemoryStream([0x7F, 0xFF, 0xFF, 0xFF, 1, 2, 3]);
        var ex = await Assert.ThrowsAsync<ProtocolException>(() => new FramedConnection(stream).ReadFrameAsync(default));
        Assert.Equal(ErrorCodes.InvalidMessage, ex.ErrorCode);
    }

    [Fact]
    public async Task Oversized_outgoing_frame_is_rejected()
    {
        var framed = new FramedConnection(new MemoryStream(), maxFrameBytes: 16);
        await Assert.ThrowsAsync<ProtocolException>(() => framed.WriteFrameAsync(new string('x', 100), default));
    }

    [Fact]
    public async Task Truncated_frame_is_reported_as_end_of_stream()
    {
        var stream = new MemoryStream([0, 0, 0, 10, 65, 66]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => new FramedConnection(stream).ReadFrameAsync(default));
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("1.12", 1, 12)]
    [InlineData("2.0", 2, 0)]
    public void Protocol_version_parses(string text, int major, int minor) =>
        Assert.Equal(new ProtocolVersion(major, minor), ProtocolVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0.0")]
    [InlineData("a.b")]
    [InlineData("-1.0")]
    public void Invalid_protocol_version_is_rejected(string text) => Assert.False(ProtocolVersion.TryParse(text, out _));

    [Theory]
    [InlineData("1.0", "1.0", true, "1.0")]
    [InlineData("1.0", "1.1", true, "1.0")]
    [InlineData("1.1", "1.0", true, "1.0")]
    [InlineData("1.0", "2.0", false, "0.0")]
    public void Protocol_versions_negotiate_within_the_same_major(string local, string peer, bool ok, string expected)
    {
        Assert.Equal(ok, ProtocolVersion.TryNegotiate(ProtocolVersion.Parse(local), ProtocolVersion.Parse(peer), out var negotiated));
        Assert.Equal(expected, negotiated.ToString());
    }

    [Fact]
    public void Current_protocol_version_is_1_0() => Assert.Equal("1.0", ProtocolConstants.Current.ToString());

    [Fact]
    public void Discovery_packet_signature_detects_tampering_and_wrong_key()
    {
        var key = ClassroomCode.DeriveKey("CLASS-8F4K-2026");
        var packet = DiscoverySigner.WithSignature(key, new DiscoveryPacket(ProtocolConstants.ServiceName, 1,
            MessageTypes.DiscoveryResponse, ProtocolConstants.DeviceTeacher, 39501, "8-A", "t", "Teacher PC", HandshakeCrypto.NewNonce(), 1, string.Empty));

        Assert.True(DiscoverySigner.Verify(key, packet));
        Assert.False(DiscoverySigner.Verify(key, packet with { Port = 39999 }));
        Assert.False(DiscoverySigner.Verify(key, packet with { Classroom = "9-B" }));
        Assert.False(DiscoverySigner.Verify(ClassroomCode.DeriveKey("CLASS-ZZZZ-9999"), packet));
    }

    [Fact]
    public void Discovery_packet_contains_no_secret()
    {
        var code = "CLASS-8F4K-2026";
        var key = ClassroomCode.DeriveKey(code);
        var json = MessageSerializer.Serialize(DiscoverySigner.WithSignature(key, new DiscoveryPacket(ProtocolConstants.ServiceName, 1,
            MessageTypes.DiscoveryResponse, ProtocolConstants.DeviceTeacher, 39501, "8-A", "t", "Teacher PC", HandshakeCrypto.NewNonce(), 1, string.Empty)));
        Assert.DoesNotContain(code, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(key), json, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(json) < ProtocolConstants.MaxDatagramBytes);
    }
}
