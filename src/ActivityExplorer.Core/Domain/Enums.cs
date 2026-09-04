namespace ActivityExplorer.Core.Domain;

public enum SportKind { Cycling = 1, Running = 2, Walking = 3 }
public enum SegmentSourceKind { Unknown = 0, Drawn = 1, Activity = 2, Route = 3, ImportedFile = 4 }
public enum SourceKind { GarminArchive = 1, StravaArchive = 2, Fit = 3, Tcx = 4, Gpx = 5, WatchedFolder = 6 }
public enum ImportStatus { Queued = 1, Running = 2, Completed = 3, CompletedWithWarnings = 4, Failed = 5, Interrupted = 6 }
public enum RecordKind
{
    Distance = 1,
    Duration = 2,
    Elevation = 3,
    AverageSpeed = 4,
    DistanceEffort = 5,
    PowerCurve = 6,
    TimedDistanceEffort = 7
}
public enum RecordScope { All = 1, Outdoor = 2 }
public enum SourceProvider { Unknown = 0, Garmin = 1, Strava = 2 }
public enum AcquisitionMethod { DirectUpload = 1, AccountExport = 2, WatchedFolder = 3 }
public enum MovingTimeSource { FitSession = 1, EstimatedFromRecords = 2, SourceSummary = 3, Unavailable = 4 }
public enum ActivityMetricOrigin { Imported = 1, Manual = 2 }
public enum ImportBatchKind { FileImport = 1, ActivityTransfer = 3, RouteImport = 4 }
public enum MapPrivacyMode { Blank = 0, OpenFreeMap = 1 }
public enum FileOperationKind { Copy = 1, OwnerQuarantine = 2, FileQuarantine = 3 }
public enum FileOperationState { Pending = 1, Prepared = 2, DatabaseCommitted = 3, Completed = 4, RolledBack = 5, Failed = 6 }
