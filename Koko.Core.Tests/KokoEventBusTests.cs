using Koko.Core.Events;

namespace Koko.Core.Tests;

public sealed class KokoEventBusTests
{
    [Test]
    public async Task Event_bus_publishes_to_matching_subscribers_and_disposes_subscription()
    {
        var bus = new KokoEventBus();
        var received = new List<string>();

        var subscription = bus.Subscribe<KokoOperationEvent>(x => received.Add($"{x.Stage}:{x.Message}"));
        bus.Publish(new KokoOperationEvent("op", "Start", "one"));
        subscription.Dispose();
        bus.Publish(new KokoOperationEvent("op", "End", "two"));

        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0]).IsEqualTo("Start:one");
    }
}
