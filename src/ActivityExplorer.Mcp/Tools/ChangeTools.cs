using System.ComponentModel;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp.Models;
using ActivityExplorer.Mcp.Configuration;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ActivityExplorer.Mcp.Tools;

[McpServerToolType]
public sealed partial class ChangeTools(McpWriteBridge bridge, McpOptions options)
{
    private Task<CallToolResult> Prepare<T>(string action, Guid? profileId, T arguments, CancellationToken token) =>
        Result(async () => await bridge.PrepareAsync(new(action, profileId, McpChangeJson.Element(arguments)), token));

    private async Task<CallToolResult> Result<T>(Func<Task<T>> action)
    {
        try
        {
            var result = ToolResultFactory.Create(await action(), null, DateTimeOffset.UtcNow);
            return JsonSerializer.SerializeToUtf8Bytes(result).Length > options.MaxResponseBytes
                ? ToolResultFactory.Error("response_too_large", "The response exceeds the size limit. Narrow the selection or use Web review; check status before repeating a write.", DateTimeOffset.UtcNow) : result;
        }
        catch (McpBridgeException exception) { return ToolResultFactory.Error(exception.Code, exception.Message, DateTimeOffset.UtcNow); }
        catch (ArgumentException exception) { return ToolResultFactory.Error("invalid_argument", exception.Message, DateTimeOffset.UtcNow); }
    }

    [McpServerTool(Name = "get_write_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpWriteContext>))]
    [Description("Read bounded write-context data: tags, saved-searches, goals, goal, metrics, assets, watched-folders, originals, activity-points, route-points or segment-points. Point contexts expose indices and distances, without GPS coordinates. File paths are not returned.")]
    public Task<CallToolResult> GetContext(Guid profileId, string kind, CancellationToken cancellationToken, Guid? entityId = null, int page = 1, int pageSize = 25) =>
        Result(() => bridge.ContextAsync(new(profileId, kind, entityId, page, pageSize), cancellationToken));

    [McpServerTool(Name = "get_prepared_change", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Read an unexpired prepared change. The plan ID identifies the preview; it does not authorize execution.")]
    public Task<CallToolResult> GetPreview(Guid planId, CancellationToken cancellationToken) => Result(() => bridge.PreviewAsync(planId, cancellationToken));

    [McpServerTool(Name = "get_operation_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpOperationStatus>))]
    [Description("Read durable write status, entity ID and import results. Always check status after a timeout or disconnection before preparing a replacement operation.")]
    public Task<CallToolResult> GetStatus(Guid operationId, CancellationToken cancellationToken) => Result(() => bridge.StatusAsync(operationId, cancellationToken));

    [McpServerTool(Name = "apply_prepared_change", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpApplyResult>))]
    [Description("Apply exactly one immutable prepared change after human confirmation through MCP elicitation, or return its Web review URL when prompting is unsupported. No tool argument can pre-confirm a change. Repeated calls return the existing operation.")]
    public Task<CallToolResult> Apply(Guid planId, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken) => Result(async () =>
    {
        try
        {
            var existing = await bridge.StatusAsync(planId, cancellationToken);
            return new McpApplyResult(existing.Status, planId, Operation: existing);
        }
        catch (McpBridgeException exception) when (exception.Code == "not_found") { }
        var preview = await bridge.PreviewAsync(planId, cancellationToken);
        if (context.Params?.InputResponses?.TryGetValue("confirmation", out var response) == true)
        {
            if (!bridge.Challenges.TryGetValue(planId, out var challenge))
                throw new McpBridgeException("confirmation_required", "The confirmation request is unavailable. Review the change again.");
            if (challenge.State != context.Params.RequestState || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new McpBridgeException("confirmation_required", "The confirmation state does not match or expired. Review the change again.");
            if (!bridge.Challenges.TryRemove(new KeyValuePair<Guid, McpWriteBridge.ConfirmationChallenge>(planId, challenge)))
                throw new McpBridgeException("confirmation_required", "The confirmation was already consumed. Check operation status.");
            var answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
            if (answer?.IsAccepted != true || answer.Content?.TryGetValue("confirm", out var confirm) != true || confirm.ValueKind != JsonValueKind.True)
            {
                await bridge.DeclineAsync(planId, cancellationToken);
                return new McpApplyResult("declined", planId);
            }
            var phrase = answer.Content.TryGetValue("confirmationPhrase", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var operation = await bridge.ApproveAsync(planId, phrase, cancellationToken);
            return new McpApplyResult(operation.Status, planId, Operation: operation);
        }
        if (!context.Server.IsMrtrSupported || context.Server.ClientCapabilities?.Elicitation?.Form is null)
            return new McpApplyResult("awaiting_web_approval", planId, preview.ReviewUrl);
        foreach (var old in bridge.Challenges.Where(item => item.Value.ExpiresAt <= DateTimeOffset.UtcNow)) bridge.Challenges.TryRemove(old.Key, out _);
        var pending = bridge.Challenges.GetOrAdd(planId, _ => new(Guid.NewGuid().ToString("N"), preview.ExpiresAt));
        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
            {
                ["confirm"] = new ElicitRequestParams.BooleanSchema { Title = "Apply this prepared change", Description = "Approve only the exact change shown above.", Default = false }
            },
            Required = ["confirm"]
        };
        if (preview.ConfirmationPhrase is not null)
        {
            schema.Properties["confirmationPhrase"] = new ElicitRequestParams.StringSchema { Title = "Confirmation phrase", Description = "Type " + preview.ConfirmationPhrase + " exactly." };
            schema.Required.Add("confirmationPhrase");
        }
        throw new InputRequiredException(inputRequests: new Dictionary<string, InputRequest>
        {
            ["confirmation"] = InputRequest.ForElicitation(new ElicitRequestParams
            { Message = preview.Summary + "\nProfiles: " + string.Join(", ", preview.ProfileIds) + "\n" + preview.Details.GetRawText(), RequestedSchema = schema })
        }, requestState: pending.State);
    });
}
