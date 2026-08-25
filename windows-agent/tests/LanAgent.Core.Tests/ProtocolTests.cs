using LanAgent.Core.Protocol;
using LanAgent.Core.Util;
using Xunit;

namespace LanAgent.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Make_fills_envelope_and_drops_null_fields()
    {
        var msg = Protocol.Make(MsgType.HEARTBEAT, "device-1", new Dictionary<string, object?>
        {
            ["agent_version"] = "1.0.0",
            ["policy_version"] = 42,
            ["hostname"] = null!
        });
        Assert.Equal(1, msg["v"]);
        Assert.Equal("HEARTBEAT", msg["type"]);
        Assert.Equal("device-1", msg["device_id"]);
        Assert.NotNull(msg["msg_id"] as string);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}", (string)msg["timestamp"]);
        Assert.False(msg.ContainsKey("hostname"), "null fields must be dropped");
        Assert.Equal(42, msg["policy_version"]);
    }

    [Fact]
    public void Parse_accepts_valid_envelope_and_ignores_unknown_fields()
    {
        string json = """
        {
          "v": 1, "type": "COMMAND", "msg_id": "m-1", "timestamp": "2026-08-25T18:30:00+05:30",
          "device_id": "device-1",
          "command_id": "c-1", "command_type": "GET_SYSTEM_INFO", "payload": {}, "attempt": 2,
          "future_field": { "nested": true }
        }
        """;
        var msg = Protocol.Parse(json);
        Assert.Equal("COMMAND", msg.Type);
        Assert.Equal("m-1", msg.MsgId);
        Assert.Equal("device-1", msg.DeviceId);
        Assert.Equal("c-1", msg.GetString("command_id"));
        Assert.Equal(2, msg.GetInt("attempt"));
        Assert.NotNull(msg.Get("payload"));
        Assert.True(msg.GetBool("future_bool", true));
        Assert.Null(msg.GetString("missing"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"type": ""}""")]
    [InlineData("""{"type": 42}""")]
    [InlineData("""[]""")]
    public void Parse_rejects_invalid_envelopes(string json)
    {
        Assert.Throws<ProtocolException>(() => Protocol.Parse(json));
    }

    [Fact]
    public void Parse_rejects_unsupported_version()
    {
        Assert.Throws<ProtocolException>(() => Protocol.Parse("""{"v": 99, "type": "PING"}"""));
    }

    [Fact]
    public void Parse_rejects_oversized_frames()
    {
        string big = """{"type": "PING", "pad": """" + new string('x', 100) + "\"}";
        Assert.Throws<ProtocolException>(() => Protocol.Parse(big, maxBytes: 50));
    }

    [Fact]
    public void Parse_rejects_invalid_json()
    {
        Assert.Throws<ProtocolException>(() => Protocol.Parse("{not json"));
    }

    [Fact]
    public void Command_catalog_covers_all_documented_types()
    {
        var expected = new[]
        {
            "GET_SYSTEM_INFO","GET_INSTALLED_APPS","INSTALL_APP","UNINSTALL_APP","UPDATE_APP","CHECK_APP",
            "APPLY_DNS","CHECK_DNS","APPLY_BROWSER_POLICY","CHECK_BROWSER_POLICY","REMOVE_BROWSER_POLICY",
            "APPLY_APP_POLICY","CHECK_APP_POLICY","SYNC_POLICY","RESTART_AGENT","RESTART_PC","SHUTDOWN_PC",
            "LOCK_PC","LOGOFF_USER","RUN_ADMIN_COMMAND","UPDATE_AGENT"
        };
        foreach (string type in expected)
            Assert.Contains(type, CommandType.All);
        Assert.Equal(expected.Length, CommandType.All.Count);
    }

    [Fact]
    public void Timestamps_use_local_offset_format()
    {
        string iso = Tx.NowIso();
        Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}", iso);
        Assert.Equal(iso, DateTimeOffset.Parse(iso).ToString("yyyy-MM-dd'T'HH:mm:sszzz"));
    }

    [Fact]
    public void Serialize_is_deterministic_roundtrip()
    {
        var msg = Protocol.Make(MsgType.COMMAND_RECEIVED, "d-1", new Dictionary<string, object?>
        {
            ["command_id"] = "c-1",
            ["duplicate"] = true
        });
        string json = Protocol.Serialize(msg);
        var parsed = Protocol.Parse(json);
        Assert.Equal("COMMAND_RECEIVED", parsed.Type);
        Assert.Equal("c-1", parsed.GetString("command_id"));
        Assert.True(parsed.GetBool("duplicate", false));
    }
}
