using System.Security.Cryptography;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Web.Services;

public sealed partial class McpWriteService
{
    internal sealed record LocalReviewPath(IReadOnlyList<TrackPoint> Points, int OriginalCount, string Digest);

    private static LocalReviewPath CreateLocalReviewPath(TrackPoint[] points)
    {
        const int maximumDisplayPoints = 512;
        var display = new TrackPoint[Math.Min(points.Length, maximumDisplayPoints)];
        for (var index = 0; index < display.Length; index++)
        {
            var point = points[(int)((long)index * (points.Length - 1) / (display.Length - 1))];
            display[index] = new(null, point.Latitude, point.Longitude, null, null, null, null, null, null, null);
        }
        return new(Array.AsReadOnly(display), points.Length, GeometryDigest(points));
    }

    private async Task<CreateSegmentPathRequest> VerifiedSegmentRequestAsync(McpChangeRequest request, CancellationToken token)
    {
        var segment = await SegmentRequestAsync(request, token);
        VerifyReviewedGeometry(request, SegmentService.ValidatePoints(segment.Points, "points"));
        return segment;
    }

    private async Task<CreateRoutePathRequest> VerifiedRouteRequestAsync(McpChangeRequest request, CancellationToken token)
    {
        var route = await RouteRequestAsync(request, token);
        VerifyReviewedGeometry(request, RouteService.ValidPoints(route.Points));
        return route;
    }

    private static void VerifyReviewedGeometry(McpChangeRequest request, TrackPoint[] points)
    {
        if (request.ReviewedGeometrySha256 is { } digest && digest != GeometryDigest(points))
            throw new InvalidOperationException("The path changed after review. Prepare the change again.");
    }

    private static string GeometryDigest(TrackPoint[] points) =>
        Convert.ToHexString(SHA256.HashData(GeometryCodec.ToWkb(points)!));
}
