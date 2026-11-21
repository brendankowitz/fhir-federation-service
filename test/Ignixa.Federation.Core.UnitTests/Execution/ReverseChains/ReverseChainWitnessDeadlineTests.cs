using System.Buffers;
using System.Reflection;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

public sealed class ReverseChainWitnessDeadlineTests
{
    [Fact]
    public async Task ExpiryDuringTheFinalWitnessDoesNotReturnPrimaryTargets()
    {
        var fixture = new ReverseChainFixture();
        ShardEntry witness = Obs("o", "Patient/a");
        using var buffer = new ReadAfterDeadline(witness.Resource.ToArray());
        var backingField = typeof(ShardEntry).GetField("<Resource>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
        backingField.SetValue(witness, (ReadOnlyMemory<byte>)buffer.GatedMemory);
        var eastPage = new TaskCompletionSource<ShardPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.OnSearch(East, (_, token) =>
        {
            buffer.Token = token;
            return eastPage.Task;
        });
        fixture.Client.OnSearch(West, Page(null));

        Task resolve = fixture.Resolver(timeout: TimeSpan.FromSeconds(2))
            .ResolveAsync(fixture.Plan(), AllTypes, 2, TestContext.Current.CancellationToken);
        fixture.Client.Calls.Count.ShouldBe(2);
        eastPage.SetResult(Page(null, witness));
        var exception = await Should.ThrowAsync<ShardFailureException>(resolve);
        exception.Reason.ShouldBe(ShardFailureReason.Timeout);
        exception.Shard.ShouldBe(East);
    }

    private sealed class ReadAfterDeadline(byte[] bytes) : MemoryManager<byte>
    {
        public CancellationToken Token { get; set; }
        public Memory<byte> GatedMemory => CreateMemory(bytes.Length);

        public override Span<byte> GetSpan()
        {
            Token.IsCancellationRequested.ShouldBeFalse();
            Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
