using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for WsConnectionManager's channel-subscription bookkeeping.
// The previous implementation had a race in RemoveAllSubscriptions — channels
// created between the .Keys snapshot and the AddOrUpdate call kept the stale
// user. These tests pin the lock-based fix.
public class WebSocketManagerTests
{
    [Fact]
    public void Subscribe_Moves_User_From_Old_Channel_To_New()
    {
        // Subscribing to a second channel implicitly unsubscribes from the first
        // (Python's behavior — a client only has one "current" channel).
        var mgr = new WsConnectionManager();
        mgr.Subscribe("alice", "channel-1");
        mgr.Subscribe("alice", "channel-2");

        var channels = mgr.Channels;
        // channel-1 should no longer contain alice (and may be pruned).
        channels.TryGetValue("channel-1", out var c1).ShouldBeFalse();
        _ = c1;
        channels["channel-2"].ShouldContain("alice");
    }

    [Fact]
    public void RemoveAllSubscriptions_Clears_All_Channels()
    {
        var mgr = new WsConnectionManager();
        mgr.Subscribe("alice", "channel-a");
        // Separate user on a second channel — not affected by alice's removal.
        mgr.Subscribe("bob", "channel-b");

        mgr.RemoveAllSubscriptions("alice");

        var channels = mgr.Channels;
        // alice's channel is pruned entirely when empty.
        channels.ContainsKey("channel-a").ShouldBeFalse();
        channels["channel-b"].ShouldContain("bob");
    }

    [Fact]
    public async Task Concurrent_Subscribe_And_Remove_Does_Not_Leak_Stale_Entries()
    {
        // Stress test: 50 concurrent threads each subscribe alice to a new
        // channel and immediately remove all subscriptions. Under the previous
        // race the .Keys snapshot could miss channels added mid-iteration,
        // leaving alice subscribed to them. After the lock-based fix, the
        // final state must have alice in at most one channel (the most recent
        // subscribe that ran after any in-flight removal).
        var mgr = new WsConnectionManager();
        var tasks = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            var ch = $"ch-{i}";
            tasks.Add(Task.Run(() =>
            {
                mgr.Subscribe("alice", ch);
                mgr.RemoveAllSubscriptions("alice");
            }));
        }
        await Task.WhenAll(tasks);

        // After all operations complete, alice should not be in any channel.
        var stillSubscribed = mgr.Channels.Count(kv => kv.Value.Contains("alice"));
        stillSubscribed.ShouldBe(0, "RemoveAllSubscriptions must not leave stale entries under concurrent mutation");
    }
}
