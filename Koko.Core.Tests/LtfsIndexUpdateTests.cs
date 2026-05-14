using Koko.Core.Ltfs;

namespace Koko.Core.Tests;

public sealed class LtfsIndexUpdateTests
{
    [Test]
    public async Task Data_partition_checkpoint_increments_generation_and_tracks_previous_location()
    {
        var source = CreateIndex(LtfsPartition.B, 623202, generation: 10);

        var checkpoint = LtfsIndexUpdater.CreateDataPartitionCheckpoint(
            source,
            startBlock: 700000,
            updateTime: DateTimeOffset.Parse("2026-05-12T00:00:00Z"));

        await Assert.That(checkpoint.GenerationNumber).IsEqualTo(11UL);
        await Assert.That(checkpoint.Location.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(checkpoint.Location.StartBlock).IsEqualTo(700000UL);
        await Assert.That(checkpoint.PreviousGenerationLocation.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(checkpoint.PreviousGenerationLocation.StartBlock).IsEqualTo(623202UL);
    }

    [Test]
    public async Task Index_partition_refresh_keeps_generation_and_preserves_data_partition_location()
    {
        var source = CreateIndex(LtfsPartition.B, 700000, generation: 11);

        var refreshed = LtfsIndexUpdater.CreateIndexPartitionRefresh(
            source,
            startBlock: 42,
            updateTime: DateTimeOffset.Parse("2026-05-12T00:01:00Z"));

        await Assert.That(refreshed.GenerationNumber).IsEqualTo(11UL);
        await Assert.That(refreshed.Location.Partition).IsEqualTo(LtfsPartition.A);
        await Assert.That(refreshed.Location.StartBlock).IsEqualTo(42UL);
        await Assert.That(refreshed.PreviousGenerationLocation.Partition).IsEqualTo(LtfsPartition.B);
        await Assert.That(refreshed.PreviousGenerationLocation.StartBlock).IsEqualTo(700000UL);
    }

    [Test]
    public async Task Checkpoint_policy_matches_legacy_byte_time_and_force_triggers()
    {
        var policy = new LtfsCheckpointPolicy(
            MaxUnindexedBytes: 1024,
            MaxUnindexedAge: TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.Parse("2026-05-12T00:10:00Z");

        await Assert.That(LtfsIndexRepository.ShouldCheckpoint(
            new LtfsIndexCounters(1024, 0, now), policy, now)).IsTrue();
        await Assert.That(LtfsIndexRepository.ShouldCheckpoint(
            new LtfsIndexCounters(0, 0, now - TimeSpan.FromMinutes(5)), policy, now)).IsTrue();
        await Assert.That(LtfsIndexRepository.ShouldCheckpoint(
            new LtfsIndexCounters(0, 0, now), policy, now, force: true)).IsTrue();
        await Assert.That(LtfsIndexRepository.ShouldCheckpoint(
            new LtfsIndexCounters(0, 0, now), policy, now)).IsFalse();
    }

    private static LtfsIndex CreateIndex(LtfsPartition partition, ulong startBlock, ulong generation)
    {
        var index = new LtfsIndex
        {
            Creator = "Koko.Core.Tests",
            VolumeUuid = Guid.Parse("129fa6c4-b043-4286-9188-0c588a94ad89"),
            GenerationNumber = generation,
            Location = new LtfsLocation { Partition = partition, StartBlock = startBlock },
            PreviousGenerationLocation = new LtfsLocation { Partition = LtfsPartition.B, StartBlock = 607920 },
            HighestFileUid = 1,
        };

        index.RootDirectories.Add(new LtfsDirectory { Name = "S00007L6", FileUid = 1 });
        return index;
    }
}
