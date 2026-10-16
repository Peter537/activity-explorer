using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record RouteFilter(Guid? OwnerId = null, string? Search = null, SportKind? Sport = null,
    string Sort = "name-asc", Guid? ImportBatchId = null);
