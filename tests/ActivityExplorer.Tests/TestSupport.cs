using System.IO.Compression;
using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Tests;

internal static class TestSupport
{
    public static string NewDirectory()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string Write(string directory, string name, string content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    public static string Gpx(string sport = "running", string name = "Morning run") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx version="1.1" creator="tests">
          <metadata><name>{name}</name></metadata>
          <trk><name>{name}</name><type>{sport}</type><trkseg>
            <trkpt lat="1.0000" lon="-30.0000"><ele>10</ele><time>2026-01-01T10:00:00+02:30</time></trkpt>
            <trkpt lat="1.0010" lon="-29.9990"><ele>15</ele><time>2026-01-01T10:01:00+02:30</time></trkpt>
            <trkpt lat="1.0020" lon="-29.9980"><ele>13</ele><time>2026-01-01T10:02:00+02:30</time></trkpt>
          </trkseg></trk>
        </gpx>
        """;

    public static string Tcx(string sport = "Biking") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <TrainingCenterDatabase xmlns="http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2">
          <Activities><Activity Sport="{sport}"><Id>2026-01-01T09:00:00Z</Id>
            <Lap StartTime="2026-01-01T09:00:00Z"><Track>
              <Trackpoint><Time>2026-01-01T09:00:00Z</Time><Position><LatitudeDegrees>1</LatitudeDegrees><LongitudeDegrees>-30</LongitudeDegrees></Position><AltitudeMeters>10</AltitudeMeters><DistanceMeters>0</DistanceMeters><HeartRateBpm><Value>120</Value></HeartRateBpm><Cadence>80</Cadence></Trackpoint>
              <Trackpoint><Time>2026-01-01T09:01:00Z</Time><Position><LatitudeDegrees>1.001</LatitudeDegrees><LongitudeDegrees>-29.999</LongitudeDegrees></Position><AltitudeMeters>20</AltitudeMeters><DistanceMeters>150</DistanceMeters><HeartRateBpm><Value>140</Value></HeartRateBpm><Cadence>90</Cadence></Trackpoint>
            </Track></Lap>
          </Activity></Activities>
        </TrainingCenterDatabase>
        """;

    public static IReadOnlyList<TrackPoint> Track(int count = 30, bool reverse = false, int gapAt = -1)
    {
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var result = Enumerable.Range(0, count).Select(index =>
        {
            var position = reverse ? count - index - 1 : index;
            var seconds = index + (index >= gapAt && gapAt >= 0 ? 120 : 0);
            return new TrackPoint(start.AddSeconds(seconds), 1 + position * 0.0001, -30 + position * 0.0001,
                position * 12, 10 + position, 4, 130 + index % 5, 85, 200 + index, 15);
        }).ToArray();
        return result;
    }

    public static string Zip(string directory, string entryName, string content)
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return path;
    }

    public static string CyclingFit(
        string directory,
        string name = "123456_ACTIVITY.fit",
        bool includePower = false,
        Dynastream.Fit.SubSport? subSport = null)
    {
        var path = Path.Combine(directory, name);
        var start = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        using var stream = File.Create(path);
        var encoder = new Dynastream.Fit.Encode(stream, Dynastream.Fit.ProtocolVersion.V20);
        var fileId = new Dynastream.Fit.FileIdMesg();
        fileId.SetType(Dynastream.Fit.File.Activity);
        fileId.SetManufacturer(1);
        fileId.SetSerialNumber(55555);
        fileId.SetTimeCreated(new Dynastream.Fit.DateTime(start));
        encoder.Write(fileId);

        for (var index = 0; index < 30; index++)
        {
            var record = new Dynastream.Fit.RecordMesg();
            record.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(index)));
            record.SetPositionLat(ToSemicircles(1 + index * 0.0001));
            record.SetPositionLong(ToSemicircles(-30 + index * 0.0001));
            record.SetDistance(index * 12);
            record.SetEnhancedAltitude(10 + index);
            record.SetEnhancedSpeed(4);
            record.SetHeartRate((byte)(130 + index % 5));
            record.SetCadence(85);
            record.SetTemperature(18);
            record.SetEnhancedRespirationRate(30 + index % 3);
            if (includePower) record.SetPower((ushort)(200 + index));
            encoder.Write(record);
        }

        var lap = new Dynastream.Fit.LapMesg();
        lap.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(29)));
        lap.SetStartTime(new Dynastream.Fit.DateTime(start));
        lap.SetTotalElapsedTime(29);
        lap.SetTotalTimerTime(29);
        lap.SetTotalMovingTime(28);
        lap.SetTotalDistance(348);
        lap.SetAvgHeartRate(132);
        encoder.Write(lap);

        var session = new Dynastream.Fit.SessionMesg();
        session.SetTimestamp(new Dynastream.Fit.DateTime(start.AddSeconds(29)));
        session.SetStartTime(new Dynastream.Fit.DateTime(start));
        session.SetSport(Dynastream.Fit.Sport.Cycling);
        if (subSport.HasValue) session.SetSubSport(subSport.Value);
        session.SetTotalElapsedTime(29);
        session.SetTotalTimerTime(29);
        session.SetTotalMovingTime(28);
        session.SetTotalDistance(348);
        session.SetTotalAscent(29);
        session.SetTotalCalories(300);
        session.SetMetabolicCalories(50);
        encoder.Write(session);
        encoder.Close();
        return path;
    }

    private static int ToSemicircles(double degrees) =>
        (int)Math.Round(degrees * 2_147_483_648d / 180d);

}
