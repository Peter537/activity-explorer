using System.ComponentModel;
using System.Text.Json;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Mcp.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ActivityExplorer.Mcp.Tools;

[McpServerToolType]
public sealed partial class ChangeTools(McpWriteBridge bridge, ToolExecutionPolicy execution)
{
    private Task<CallToolResult> Prepare<T>(string action, Guid? profileId, T arguments, CancellationToken token) =>
        Result("prepare_" + action, boundedToken => bridge.PrepareAsync(new(action, profileId, McpChangeJson.Element(arguments)), boundedToken), token);

    private Task<CallToolResult> Result<T>(string name, Func<CancellationToken, Task<T>> action, CancellationToken token) where T : class =>
        execution.RunAsync<T>(name, null, async (_, boundedToken) => await action(boundedToken), token);

    [McpServerTool(Name = "get_write_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpWriteContext>))]
    [Description("Read bounded write-context data: tags, saved-searches, goals, goal, metrics, assets, watched-folders, originals, activity-points, route-points or segment-points. Point contexts expose indices and distances, without GPS coordinates. File paths are not returned.")]
    public Task<CallToolResult> GetContext(Guid profileId, string kind, CancellationToken cancellationToken, Guid? entityId = null, int page = 1, int pageSize = 25) =>
        Result("get_write_context", token => bridge.ContextAsync(new(profileId, kind, entityId, page, pageSize), token), cancellationToken);

    [McpServerTool(Name = "get_prepared_change", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpChangePreview>))]
    [Description("Read an unexpired prepared change. The plan ID identifies the preview; it does not authorize execution.")]
    public Task<CallToolResult> GetPreview(Guid planId, CancellationToken cancellationToken) => Result("get_prepared_change", token => bridge.PreviewAsync(planId, token), cancellationToken);

    [McpServerTool(Name = "get_operation_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpOperationStatus>))]
    [Description("Read durable write status, entity ID and import results. Always check status after a timeout or disconnection before preparing a replacement operation.")]
    public Task<CallToolResult> GetStatus(Guid operationId, CancellationToken cancellationToken) => Result("get_operation_status", token => bridge.StatusAsync(operationId, token), cancellationToken);

    [McpServerTool(Name = "apply_prepared_change", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<McpApplyResult>))]
    [Description("Apply exactly one immutable prepared change after human confirmation through MCP elicitation, or return its Web review URL when prompting is unsupported. No tool argument can pre-confirm a change. Repeated calls return the existing operation.")]
    public Task<CallToolResult> Apply(Guid planId, RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken) => Result("apply_prepared_change", async token =>
    {
        try
        {
            var existing = await bridge.StatusAsync(planId, token);
            return new McpApplyResult(existing.Status, planId, Operation: existing);
        }
        catch (McpBridgeException exception) when (exception.Code == "not_found") { }
        var preview = await bridge.PreviewAsync(planId, token);
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
                await bridge.DeclineAsync(planId, token);
                return new McpApplyResult("declined", planId);
            }
            var phrase = answer.Content.TryGetValue("confirmationPhrase", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            var operation = await bridge.ApproveAsync(planId, phrase, token);
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
    }, cancellationToken);
}
