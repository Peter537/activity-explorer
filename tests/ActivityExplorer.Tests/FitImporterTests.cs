using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using Dynastream.Fit;
using FitDateTime = Dynastream.Fit.DateTime;
using FitFile = Dynastream.Fit.File;

namespace ActivityExplorer.Tests;

public sealed class FitImporterTests
{
    [Fact]
    public async Task Official_sdk_generated_fit_is_parsed()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "ride.fit");
        var start = new System.DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var stream = System.IO.File.Create(path))
        {
            var encoder = new Encode(stream, ProtocolVersion.V20);
            var fileId = new FileIdMesg();
            fileId.SetType(FitFile.Activity);
            fileId.SetManufacturer(1);
            fileId.SetProductName("Test device");
            fileId.SetSerialNumber(12345);
            fileId.SetTimeCreated(new FitDateTime(start));
            encoder.Write(fileId);

            for (var index = 0; index < 3; index++)
            {
                var record = new RecordMesg();
                record.SetTimestamp(new FitDateTime(start.AddSeconds(index * 30)));
                record.SetPositionLat(ToSemicircles(1 + index * 0.001));
                record.SetPositionLong(ToSemicircles(-30 + index * 0.001));
                record.SetDistance(index * 150);
                record.SetEnhancedAltitude(10 + index * 5);
                record.SetEnhancedSpeed(5);
                record.SetHeartRate((byte)(120 + index * 5));
                record.SetCadence((byte)85);
                record.SetPower((ushort)(200 + index * 10));
                record.SetTemperature((sbyte)(18 + index));
                record.SetEnhancedRespirationRate((float)(30 + index));
                encoder.Write(record);
            }

            var session = new SessionMesg();
            session.SetTimestamp(new FitDateTime(start.AddSeconds(60)));
            session.SetStartTime(new FitDateTime(start));
            session.SetSport(Sport.Cycling);
            session.SetSubSport(SubSport.Road);
            session.SetTotalElapsedTime(60);
            session.SetTotalTimerTime(60);
            session.SetTotalDistance(300);
            session.SetTotalMovingTime(50);
            session.SetTotalDescent(7);
            session.SetEnhancedMinAltitude(10);
            session.SetEnhancedMaxAltitude(20);
            session.SetTotalCalories(500);
            session.SetMetabolicCalories(100);
            session.SetMaxCadence(95);
            session.SetTotalCycles(255);
            session.SetTotalTrainingEffect(3.4f);
            session.SetTotalAnaerobicTrainingEffect(1.2f);
            session.SetTotalFatCalories(50);
            session.SetThresholdPower(250);
            session.SetTrainingStressScore(75.5f);
            session.SetIntensityFactor(0.85f);
            session.SetTotalAscent(10);
            session.SetAvgPower(210);
            session.SetMaxPower(220);
            session.SetAvgHeartRate(125);
            encoder.Write(session);
            encoder.Close();
        }

        var activity = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Parsed;
        Assert.Equal(SportKind.Cycling, activity.Sport);
        Assert.Equal(300, activity.DistanceMeters);
        Assert.False(activity.IsIndoor);
        Assert.Equal(3, activity.Points.Count);
        Assert.Equal(5, activity.AverageSpeedMetersPerSecond);
        Assert.Equal(5, activity.MaxSpeedMetersPerSecond);
        Assert.Equal(130, activity.MaxHeartRate);
        Assert.Equal(85, activity.AverageCadence);
        Assert.Equal(210, activity.AveragePowerWatts);
        Assert.Null(activity.ExternalId);
        Assert.Equal(60, activity.TimerTimeSeconds);
        Assert.Equal(50, activity.MovingTimeSeconds);
        Assert.Equal(MovingTimeSource.FitSession, activity.MovingTimeSource);
        Assert.Equal(7, activity.ElevationLossMeters);
        Assert.Equal(10, activity.MinElevationMeters);
        Assert.Equal(20, activity.MaxElevationMeters);
        Assert.Equal(500, activity.Calories);
        Assert.Equal(100, activity.RestingCalories);
        Assert.Equal(400, activity.ActiveCalories);
        Assert.Equal(95, activity.MaxCadence);
        Assert.Equal(255, activity.PedalRevolutions);
        Assert.Equal(19, activity.AverageTemperatureCelsius);
        Assert.Equal(31, activity.AverageRespirationRate);
        Assert.Equal(3.4, activity.AerobicTrainingEffect!.Value, 1);
        Assert.Equal(1.2, activity.AnaerobicTrainingEffect!.Value, 1);
        Assert.Contains(activity.Metrics, metric =>
            metric.Key == "fit.total_fat_calories" && metric.NumericValue == 50 && metric.Unit == "kcal");
        Assert.Contains(activity.Metrics, metric =>
            metric.Key == "fit.threshold_power" && metric.NumericValue == 250 && metric.Unit == "W");
        Assert.Contains(activity.Metrics, metric => metric.Key == "fit.training_stress_score");
        Assert.Equal(SourceProvider.Unknown, (await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Single().Provider);
        Assert.Equal("123456", FitActivityImporter.ExtractGarminActivityId("123456_ACTIVITY.fit"));
    }


    [Theory]
    [InlineData("123456_ACTIVITY.fit", "123456")]
    [InlineData("nested/123456_activity.FIT", "123456")]
    [InlineData("12345.fit", null)]
    [InlineData("device-12345_ACTIVITY.fit", null)]
    public void Garmin_activity_id_comes_only_from_export_filename(string fileName, string? expected) =>
        Assert.Equal(expected, FitActivityImporter.ExtractGarminActivityId(fileName));

    [Fact]
    public async Task Fit_without_power_keeps_power_absent_and_respiration_available()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "123456_ACTIVITY.fit");
        var start = new System.DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);
        using (var stream = System.IO.File.Create(path))
        {
            var encoder = new Encode(stream, ProtocolVersion.V20);
            var fileId = new FileIdMesg();
            fileId.SetType(FitFile.Activity);
            fileId.SetManufacturer(1);
            fileId.SetSerialNumber(99999);
            fileId.SetTimeCreated(new FitDateTime(start));
            encoder.Write(fileId);

            for (var index = 0; index < 3; index++)
            {
                var record = new RecordMesg();
                record.SetTimestamp(new FitDateTime(start.AddSeconds(index * 10)));
                record.SetPositionLat(ToSemicircles(1 + index * 0.0001));
                record.SetPositionLong(ToSemicircles(-30 + index * 0.0001));
                record.SetDistance(index * 50);
                record.SetEnhancedSpeed(5);
                record.SetEnhancedRespirationRate((float)(28 + index));
                encoder.Write(record);
            }

            var session = new SessionMesg();
            session.SetTimestamp(new FitDateTime(start.AddSeconds(20)));
            session.SetStartTime(new FitDateTime(start));
            session.SetSport(Sport.Cycling);
            session.SetTotalElapsedTime(20);
            session.SetTotalTimerTime(15);
            session.SetTotalDistance(100);
            encoder.Write(session);
            encoder.Close();
        }

        var candidate = Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit));
        Assert.Equal("123456", candidate.ExternalId);
        Assert.Equal(SourceProvider.Garmin, candidate.Provider);
        Assert.All(candidate.Parsed.Points, point => Assert.Null(point.PowerWatts));
        Assert.Null(candidate.Parsed.AveragePowerWatts);
        Assert.Null(candidate.Parsed.MaxPowerWatts);
        Assert.Equal(5, candidate.Parsed.AverageSpeedMetersPerSecond);
        Assert.Equal(5, candidate.Parsed.MaxSpeedMetersPerSecond);
        Assert.Equal(29, candidate.Parsed.AverageRespirationRate);
        Assert.Equal(15, candidate.Parsed.TimerTimeSeconds);
        Assert.Equal(15, candidate.Parsed.MovingTimeSeconds);
        Assert.Equal(MovingTimeSource.EstimatedFromRecords, candidate.Parsed.MovingTimeSource);
    }

    [Theory]
    [InlineData(SubSport.Treadmill, true)]
    [InlineData(SubSport.Spin, true)]
    [InlineData(SubSport.IndoorCycling, true)]
    [InlineData(SubSport.VirtualActivity, true)]
    [InlineData(SubSport.Road, false)]
    [InlineData(SubSport.GravelCycling, false)]
    public async Task Fit_sub_sports_provide_indoor_classification(SubSport subSport, bool expected)
    {
        Assert.Equal(expected, await ReadIndoorClassificationAsync(subSport));
    }

    [Fact]
    public async Task Generic_fit_sub_sport_defers_to_the_gps_fallback()
    {
        Assert.Null(await ReadIndoorClassificationAsync(SubSport.Generic));
    }
    [Fact]
    public async Task Invalid_fit_crc_is_rejected()
    {
        var directory = TestSupport.NewDirectory();
        var path = TestSupport.Write(directory, "bad.fit", "not a fit file");
        await Assert.ThrowsAsync<InvalidDataException>(() => new FitActivityImporter().ReadAsync(path, SourceKind.Fit));
    }

    private static async Task<bool?> ReadIndoorClassificationAsync(SubSport subSport)
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, $"{subSport}.fit");
        var start = new System.DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        using (var stream = System.IO.File.Create(path))
        {
            var encoder = new Encode(stream, ProtocolVersion.V20);
            var fileId = new FileIdMesg();
            fileId.SetType(FitFile.Activity);
            fileId.SetTimeCreated(new FitDateTime(start));
            encoder.Write(fileId);
            var session = new SessionMesg();
            session.SetTimestamp(new FitDateTime(start.AddMinutes(1)));
            session.SetStartTime(new FitDateTime(start));
            session.SetSport(Sport.Cycling);
            session.SetSubSport(subSport);
            session.SetTotalElapsedTime(60);
            session.SetTotalTimerTime(60);
            encoder.Write(session);
            encoder.Close();
        }

        return Assert.Single(await new FitActivityImporter().ReadAsync(path, SourceKind.Fit)).Parsed.IsIndoor;
    }
    private static int ToSemicircles(double degrees) => (int)Math.Round(degrees * 2_147_483_648d / 180d);
}
