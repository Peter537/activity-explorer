using System.Security.Cryptography;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class StorageLifecycleTests
{
    private const string OriginalContents = "managed original contents";
    private static readonly string OriginalSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OriginalContents)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_retries_restore_for_a_shared_original(bool movedBeforeFailure)
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var original = await WriteOriginalAsync(setup, ownerId);
        var removedReference = await AddReferenceAsync(setup, ownerId, original);
        var retainedReference = await AddReferenceAsync(setup, ownerId, original);
        var operationId = Assert.IsType<Guid>(await setup.FileOperations.QuarantineFileAsync(
            ownerId, removedReference, setup.Originals.ToStoredPath(original)));
        var quarantine = Path.Combine(setup.Paths.QuarantinePath, operationId.ToString("N"), Path.GetFileName(original));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.SourceFiles.Remove(await db.SourceFiles.SingleAsync(row => row.Id == removedReference));
            await db.SaveChangesAsync();
        }

        var failingRecovery = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            MoveFile = (from, to) =>
            {
                if (movedBeforeFailure) File.Move(from, to);
                throw new IOException("Simulated restore I/O failure.");
            }
        };

        await failingRecovery.RecoverAsync();

        var interrupted = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.Prepared, interrupted.State);
        Assert.Equal("Simulated restore I/O failure.", interrupted.ErrorMessage);
        Assert.Equal(movedBeforeFailure, File.Exists(original));
        Assert.Equal(!movedBeforeFailure, File.Exists(quarantine));

        await setup.FileOperations.RecoverAsync();
        await setup.FileOperations.RecoverAsync();

        var restored = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.RolledBack, restored.State);
        Assert.Null(restored.ErrorMessage);
        Assert.Equal(OriginalContents, await File.ReadAllTextAsync(original));
        Assert.False(Directory.Exists(Path.GetDirectoryName(quarantine)));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(retainedReference, (await verification.SourceFiles.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Recovery_retries_purge_with_current_original_references(bool referencedOnRetry, bool deletedBeforeFailure)
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var original = await WriteOriginalAsync(setup, ownerId);
        var operationId = Assert.IsType<Guid>(await setup.FileOperations.QuarantineFileAsync(
            ownerId, null, setup.Originals.ToStoredPath(original)));
        var quarantine = Path.Combine(setup.Paths.QuarantinePath, operationId.ToString("N"), Path.GetFileName(original));
        var failingRecovery = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            DeleteFile = path =>
            {
                if (deletedBeforeFailure) File.Delete(path);
                throw new UnauthorizedAccessException("Simulated quarantine access denial.");
            }
        };

        await failingRecovery.RecoverAsync();

        var interrupted = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.DatabaseCommitted, interrupted.State);
        Assert.Equal("Simulated quarantine access denial.", interrupted.ErrorMessage);
        Assert.Equal(!deletedBeforeFailure, File.Exists(quarantine));
        Assert.False(File.Exists(original));
        if (referencedOnRetry) await AddReferenceAsync(setup, ownerId, original);

        await setup.FileOperations.RecoverAsync();
        await setup.FileOperations.RecoverAsync();

        var recovered = await ReadOperationAsync(setup, operationId);
        Assert.Equal(referencedOnRetry ? FileOperationState.RolledBack : FileOperationState.Completed, recovered.State);
        Assert.Null(recovered.ErrorMessage);
        Assert.False(Directory.Exists(Path.GetDirectoryName(quarantine)));
        Assert.Equal(referencedOnRetry, File.Exists(original));
        if (referencedOnRetry) Assert.Equal(OriginalContents, await File.ReadAllTextAsync(original));
    }

    [Fact]
    public async Task Recovery_retries_owner_quarantine_purge_after_an_io_failure()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var original = await WriteOriginalAsync(setup, ownerId);
        var operationId = Assert.IsType<Guid>(await setup.FileOperations.QuarantineOwnerAsync(ownerId));
        var quarantine = Path.Combine(setup.Paths.QuarantinePath, operationId.ToString("N"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Owners.Remove(await db.Owners.SingleAsync(row => row.Id == ownerId));
            await db.SaveChangesAsync();
        }
        var failingRecovery = new FileOperationCoordinator(setup.Factory, setup.Paths, setup.Originals,
            NullLogger<FileOperationCoordinator>.Instance)
        {
            DeleteDirectory = (_, _) => throw new IOException("Simulated owner quarantine I/O failure.")
        };

        await failingRecovery.RecoverAsync();

        var interrupted = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.DatabaseCommitted, interrupted.State);
        Assert.Equal("Simulated owner quarantine I/O failure.", interrupted.ErrorMessage);
        Assert.Equal(OriginalContents, await File.ReadAllTextAsync(Path.Combine(quarantine, Path.GetFileName(original))));

        await setup.FileOperations.RecoverAsync();

        var completed = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.Completed, completed.State);
        Assert.Null(completed.ErrorMessage);
        Assert.False(Directory.Exists(quarantine));
    }

    [Fact]
    public async Task Recovery_fails_a_conflicting_restore_and_preserves_both_files()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var original = await WriteOriginalAsync(setup, ownerId);
        var sourceId = await AddReferenceAsync(setup, ownerId, original);
        var operationId = Assert.IsType<Guid>(await setup.FileOperations.QuarantineFileAsync(
            ownerId, sourceId, setup.Originals.ToStoredPath(original)));
        var quarantine = Path.Combine(setup.Paths.QuarantinePath, operationId.ToString("N"), Path.GetFileName(original));
        await File.WriteAllTextAsync(original, "original created after quarantine");

        await setup.FileOperations.RecoverAsync();

        var failed = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.Failed, failed.State);
        Assert.Contains("both exist", failed.ErrorMessage);
        Assert.Equal("original created after quarantine", await File.ReadAllTextAsync(original));
        Assert.Equal(OriginalContents, await File.ReadAllTextAsync(quarantine));

        await setup.FileOperations.RecoverAsync();
        Assert.Equal(failed.UpdatedAtUtc, (await ReadOperationAsync(setup, operationId)).UpdatedAtUtc);
    }

    [Fact]
    public async Task Recovery_fails_quarantine_with_a_target_outside_its_operation_directory()
    {
        await using var setup = await StorageSetup.CreateAsync();
        var ownerId = await setup.AddOwnerAsync();
        var original = await WriteOriginalAsync(setup, ownerId);
        var operationId = Assert.IsType<Guid>(await setup.FileOperations.QuarantineFileAsync(
            ownerId, null, setup.Originals.ToStoredPath(original)));
        var quarantine = Path.Combine(setup.Paths.QuarantinePath, operationId.ToString("N"), Path.GetFileName(original));
        var unrelatedDirectory = Path.Combine(setup.Paths.QuarantinePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelatedDirectory);
        var unrelated = Path.Combine(unrelatedDirectory, Path.GetFileName(original));
        await File.WriteAllTextAsync(unrelated, "untracked quarantine contents");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var operation = await db.FileOperations.SingleAsync(row => row.Id == operationId);
            operation.TargetRelativePath = setup.Relative(unrelated);
            await db.SaveChangesAsync();
        }

        await setup.FileOperations.RecoverAsync();

        var failed = await ReadOperationAsync(setup, operationId);
        Assert.Equal(FileOperationState.Failed, failed.State);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Equal("untracked quarantine contents", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(OriginalContents, await File.ReadAllTextAsync(quarantine));
    }

    private static async Task<string> WriteOriginalAsync(StorageSetup setup, Guid ownerId)
    {
        var path = setup.Originals.GetOriginalTarget(ownerId, OriginalSha256, ".fit");
        await File.WriteAllTextAsync(path, OriginalContents);
        return path;
    }

    private static async Task<Guid> AddReferenceAsync(StorageSetup setup, Guid ownerId, string original)
    {
        await using var db = await setup.Factory.CreateDbContextAsync();
        var source = new SourceFile
        {
            OwnerId = ownerId,
            ImportBatch = Batch(ownerId, ImportStatus.Completed, string.Empty),
            SourceKind = SourceKind.Fit,
            Provider = SourceProvider.Garmin,
            OriginalName = "source.fit",
            StoredPath = setup.Originals.ToStoredPath(original),
            Sha256 = OriginalSha256,
            Length = OriginalContents.Length
        };
        db.SourceFiles.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private static async Task<FileOperationJournal> ReadOperationAsync(StorageSetup setup, Guid id)
    {
        await using var db = await setup.Factory.CreateDbContextAsync();
        return await db.FileOperations.SingleAsync(operation => operation.Id == id);
    }
}
