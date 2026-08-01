using System.Text.Json;
using DiscordSky.Bot.Orchestration.Autonomy;

namespace DiscordSky.Tests;

public sealed class StewardOperationEvidenceTests
{
    [Fact]
    public void Parse_VerifiesStewardOperationAgainstCanonicalSkyDispatch()
    {
        var arguments = "{\"input\":{\"name\":\"laboratory\"},\"reason\":\"stranger\",\"request_id\":\"01900000-0000-7000-8000-000000000001\"}";
        var call = new WorldAutonomyToolCall(
            "call-1", "run-1", 1, "create_text_channel", "01900000-0000-7000-8000-000000000001",
            arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
            DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
            {
              "outcome": "ok",
              "data": {
                "requestId": "01900000-0000-7000-8000-000000000001",
                "kind": "create_text_channel",
                "status": "succeeded",
                "errorCode": null,
                "invocationJson": "{\"schemaVersion\":1,\"kind\":\"create_text_channel\",\"requestId\":\"01900000-0000-7000-8000-000000000001\",\"resourceId\":\"667956000757776386\",\"reason\":\"stranger\",\"arguments\":{\"request_id\":\"01900000-0000-7000-8000-000000000001\",\"reason\":\"stranger\",\"input\":{\"name\":\"laboratory\"}}}"
              }
            }
            """);

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("succeeded", evidence.Status);
        Assert.Equal("create_text_channel", evidence.Kind);
    }

    [Fact]
    public void Parse_RejectsMismatchedOperationArguments()
    {
        var arguments = "{\"request_id\":\"01900000-0000-7000-8000-000000000001\"}";
        var call = new WorldAutonomyToolCall(
            "call-1", "run-1", 1, "update_channel", "01900000-0000-7000-8000-000000000001",
            arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
            DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "requestId": "01900000-0000-7000-8000-000000000001",
                "kind": "update_channel",
                "status": "succeeded",
                "invocationJson": "{\"kind\":\"update_channel\",\"arguments\":{\"request_id\":\"01900000-0000-7000-8000-000000000999\"}}"
              }
            }
            """);

        Assert.Throws<InvalidOperationException>(() => StewardOperationEvidence.Parse(document.RootElement, call));
    }

      [Fact]
      public void Parse_MatchesNormalizedMcpSchemaArguments()
      {
        var arguments = """
          {"color_hex":null,"expected_roles_state_digest":"roles","icon_asset_id":null,"is_hoisted":false,"is_mentionable":false,"name":"canary","permissions":[],"reason":"create canary role","request_id":"01900000-0000-7000-8000-000000000001","secondary_color_hex":null,"tertiary_color_hex":null,"unicode_emoji":null}
          """;
        var call = new WorldAutonomyToolCall(
          "call-1", "run-1", 1, "create_role", "01900000-0000-7000-8000-000000000001",
          arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
          DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
          {
            "outcome": "ok",
            "data": {
            "requestId": "01900000-0000-7000-8000-000000000001",
            "kind": "create_role",
            "status": "succeeded",
            "errorCode": null,
            "invocationJson": "{\"schemaVersion\":1,\"kind\":\"create_role\",\"requestId\":\"01900000-0000-7000-8000-000000000001\",\"resourceId\":\"100000000000000001\",\"reason\":\"create canary role\",\"arguments\":{\"colorHex\":null,\"expectedRolesStateDigest\":\"roles\",\"iconAssetId\":null,\"isHoisted\":false,\"isMentionable\":false,\"name\":\"canary\",\"permissions\":[],\"secondaryColorHex\":null,\"tertiaryColorHex\":null,\"unicodeEmoji\":null}}"
            }
          }
          """);

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("succeeded", evidence.Status);
        Assert.Equal("create_role", evidence.Kind);
      }

      [Fact]
      public void Parse_MatchesArgumentsWrappedInAnInputObject()
      {
        // Regression: create_webhook nests its payload under "input" and Steward's journal stores that
        // payload unwrapped, keeping channelId inline rather than lifting it to resourceId. Recovery used to
        // assume a flat root, so six live webhook dispatches were stuck at "accepted" forever and the
        // recovery service logged a mismatch on every pass. Payload captured from the real ledger.
        var arguments = """
          {"input":{"avatarAssetId":null,"channelId":"100000000000000001","expectedChannelStateDigest":"synthetic-digest","name":"Test Herald"},"reason":"Create a synthetic test webhook.","request_id":"01900000-0000-7000-8000-000000000004"}
          """;
        var call = new WorldAutonomyToolCall(
          "call-1", "run-1", 2, "create_webhook", "01900000-0000-7000-8000-000000000004",
          arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
          DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
          {
            "outcome": "ok",
            "data": {
            "requestId": "01900000-0000-7000-8000-000000000004",
            "kind": "create_webhook",
            "status": "succeeded",
            "errorCode": null,
            "invocationJson": "{\"schemaVersion\":1,\"kind\":\"create_webhook\",\"requestId\":\"01900000-0000-7000-8000-000000000004\",\"resourceId\":\"100000000000000001\",\"reason\":\"Create a synthetic test webhook.\",\"arguments\":{\"avatarAssetId\":null,\"channelId\":\"100000000000000001\",\"expectedChannelStateDigest\":\"synthetic-digest\",\"name\":\"Test Herald\"}}"
            }
          }
          """);

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("succeeded", evidence.Status);
        Assert.Equal("create_webhook", evidence.Kind);
      }

      [Fact]
      public void Parse_TreatsStewardsNullPaddedArgumentsAsEquivalent()
      {
        // Regression: Steward materializes its full typed request record, so the journal carries explicit
        // nulls for arguments the model never sent (appliedTagIds, embeds, poll, usernameOverride, ...).
        // Sky records only the wire arguments, so equality failed on the padding alone. This fixture keeps
        // the representative runtime shape while using entirely synthetic values.
        var arguments = """
          {"input":{"allowedMentions":{"everyone":false,"repliedUser":false,"roleIds":[],"userIds":[]},"assetIds":[],"content":"A synthetic decree.","expectedWebhookStateDigest":"synthetic-digest","storedWebhookId":"vault-test-webhook","suppressEmbeds":false,"suppressNotifications":false,"threadId":null,"tts":false},"reason":"Test message delivery.","request_id":"01900000-0000-7000-8000-000000000001"}
          """;
        var call = new WorldAutonomyToolCall(
          "call-1", "run-1", 3, "send_webhook_message", "01900000-0000-7000-8000-000000000001",
          arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
          DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
          {
            "outcome": "ok",
            "data": {
            "requestId": "01900000-0000-7000-8000-000000000001",
            "kind": "send_webhook_message",
            "status": "succeeded",
            "errorCode": null,
            "invocationJson": "{\"schemaVersion\":1,\"kind\":\"send_webhook_message\",\"requestId\":\"01900000-0000-7000-8000-000000000001\",\"resourceId\":\"vault-test-webhook\",\"reason\":\"Test message delivery.\",\"arguments\":{\"allowedMentions\":{\"everyone\":false,\"repliedUser\":false,\"roleIds\":[],\"userIds\":[]},\"appliedTagIds\":null,\"assetIds\":[],\"avatarUrlOverride\":null,\"components\":null,\"content\":\"A synthetic decree.\",\"embeds\":null,\"expectedAvailableTagsDigest\":null,\"expectedWebhookStateDigest\":\"synthetic-digest\",\"poll\":null,\"storedWebhookId\":\"vault-test-webhook\",\"suppressEmbeds\":false,\"suppressNotifications\":false,\"threadId\":null,\"threadName\":null,\"tts\":false,\"usernameOverride\":null}}"
            }
          }
          """);

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("succeeded", evidence.Status);
        Assert.Equal("send_webhook_message", evidence.Kind);
      }

      [Fact]
      public void Parse_StillRejectsAJournalArgumentSkyNeverApproved()
      {
        // The null-padding allowance must not become a hole: a non-null argument present only on the
        // Steward side means Steward did something Sky never durably approved.
        var arguments = """
          {"input":{"content":"hello","storedWebhookId":"whsec_1"},"reason":"say hello","request_id":"15c8ba4d-a736-4ccd-b1a4-c4ab2251ef6a"}
          """;
        var call = new WorldAutonomyToolCall(
          "call-1", "run-1", 3, "send_webhook_message", "15c8ba4d-a736-4ccd-b1a4-c4ab2251ef6a",
          arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
          DateTimeOffset.UtcNow, null, null, null);
        using var document = JsonDocument.Parse("""
          {
            "outcome": "ok",
            "data": {
            "requestId": "15c8ba4d-a736-4ccd-b1a4-c4ab2251ef6a",
            "kind": "send_webhook_message",
            "status": "succeeded",
            "errorCode": null,
            "invocationJson": "{\"schemaVersion\":1,\"kind\":\"send_webhook_message\",\"requestId\":\"15c8ba4d-a736-4ccd-b1a4-c4ab2251ef6a\",\"resourceId\":\"whsec_1\",\"reason\":\"say hello\",\"arguments\":{\"content\":\"hello\",\"storedWebhookId\":\"whsec_1\",\"usernameOverride\":\"Somebody Else\"}}"
            }
          }
          """);

        Assert.Throws<InvalidOperationException>(() => StewardOperationEvidence.Parse(document.RootElement, call));
      }

      [Fact]
      public void Parse_TreatsTypedEmptyCollectionsAsOmittedForChannelUpdate()
      {
        var arguments = """
          {"channel_id":"100000000000000001","expected_state_digest":"digest","reason":"test update","request_id":"01900000-0000-7000-8000-000000000002","set":{"name":"annexed-laboratory","slowModeSeconds":5,"topic":"synthetic topic"}}
          """;
        var call = Call("update_channel", "01900000-0000-7000-8000-000000000002", arguments);
        using var document = Envelope(
          "update_channel",
          "01900000-0000-7000-8000-000000000002",
          "100000000000000001",
          "test update",
          "{\"clear\":[],\"expectedStateDigest\":\"digest\",\"set\":{\"availableTags\":null,\"name\":\"annexed-laboratory\",\"slowModeSeconds\":5,\"topic\":\"synthetic topic\"}}");

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("update_channel", evidence.Kind);
      }

      [Fact]
      public void Parse_TreatsTypedFalseAndEmptyMessageDefaultsAsOmitted()
      {
        var arguments = """
          {"channel_id":"100000000000000001","input":{"allowedMentions":{"everyone":false,"repliedUser":true,"roleIds":[],"userIds":[]},"assetIds":[],"content":"A decree.","expectedChannelStateDigest":"digest","mode":"reply","nonce":"test-nonce","sourceChannelId":"100000000000000001","sourceMessageId":"100000000000000002","suppressNotifications":false},"reason":"speak","request_id":"01900000-0000-7000-8000-000000000003"}
          """;
        var call = Call("send_message", "01900000-0000-7000-8000-000000000003", arguments);
        using var document = Envelope(
          "send_message",
          "01900000-0000-7000-8000-000000000003",
          "100000000000000001",
          "speak",
          "{\"allowedMentions\":{\"everyone\":false,\"repliedUser\":true,\"roleIds\":[],\"userIds\":[]},\"assetIds\":[],\"components\":null,\"content\":\"A decree.\",\"embeds\":null,\"expectedChannelStateDigest\":\"digest\",\"mode\":\"reply\",\"nonce\":\"test-nonce\",\"sourceChannelId\":\"100000000000000001\",\"sourceMessageId\":\"100000000000000002\",\"suppressEmbeds\":false,\"suppressNotifications\":false,\"tts\":false}");

        var evidence = StewardOperationEvidence.Parse(document.RootElement, call);

        Assert.Equal("send_message", evidence.Kind);
      }

      private static WorldAutonomyToolCall Call(string toolName, string requestId, string arguments) => new(
        "call-1", "run-1", 1, toolName, requestId,
        arguments, WorldAutonomyCanonicalizer.ComputeDigest(arguments), "schema", "accepted",
        DateTimeOffset.UtcNow, null, null, null);

      private static JsonDocument Envelope(
        string toolName,
        string requestId,
        string resourceId,
        string reason,
        string arguments)
      {
        var invocation = JsonSerializer.Serialize(new
        {
          schemaVersion = 1,
          kind = toolName,
          requestId,
          resourceId,
          reason,
          arguments = JsonDocument.Parse(arguments).RootElement
        });
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
          outcome = "ok",
          data = new
          {
            requestId,
            kind = toolName,
            status = "succeeded",
            errorCode = (string?)null,
            invocationJson = invocation
          }
        }));
      }
}