using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IActivityRangeAnalyzer
{
    ActivityRangeAnalysis Analyze(IReadOnlyList<TrackPoint> points, ActivityRange range, CancellationToken cancellationToken = default);
    ActivityMapProjection ProjectMap(IReadOnlyList<TrackPoint> points, ActivityRange? range = null, CancellationToken cancellationToken = default);
}
