using System.Collections.Generic;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The server bake's cap on terrain requests waiting in the game's heightmap builder (ServerBakeBuilderRequests):
/// half the builder's 16 ready slots, a zone holding its slot from its first ask until it is released, and a zone
/// refused by the cap never asked (so its build is not queued and no try is spent).
/// </summary>
public class ServerBakeBuilderRequestsTests
{
    /// <summary>HeightmapBuilder.m_maxReadyQueue in the game (game build 26249040).</summary>
    private const int GameReadySlots = 16;

    private static ServerBakeBuilderRequests Requests() =>
        new(ServerBakeBuilderRequests.HalfTheReadyList(GameReadySlots));

    [Fact]
    public void TheCapIsHalfTheBuildersReadyList()
    {
        Assert.Equal(8, ServerBakeBuilderRequests.HalfTheReadyList(GameReadySlots));
        Assert.Equal(8, Requests().Max);
    }

    [Fact]
    public void AZoneOverTheCapIsNotAsked()
    {
        var requests = Requests();
        for (int i = 0; i < 8; i++)
            Assert.Equal(ServerBakeBuilderRequests.Answer.Building, requests.Ask(new Vector2s(i, 0), () => false));
        bool asked = false;
        Assert.Equal(ServerBakeBuilderRequests.Answer.Capped, requests.Ask(new Vector2s(9, 0), () => { asked = true; return false; }));
        Assert.False(asked);
        Assert.Equal(8, requests.Count);
    }

    [Fact]
    public void AZoneAskedAlreadyKeepsItsPlaceWhenTheCapIsFull()
    {
        var requests = Requests();
        for (int i = 0; i < 8; i++) requests.Ask(new Vector2s(i, 0), () => false);
        // Coming back round the queue: still waiting, then built.
        Assert.Equal(ServerBakeBuilderRequests.Answer.Building, requests.Ask(new Vector2s(3, 0), () => false));
        Assert.Equal(ServerBakeBuilderRequests.Answer.Ready, requests.Ask(new Vector2s(3, 0), () => true));
        Assert.Equal(8, requests.Count);
    }

    [Fact]
    public void AZoneReadyAtItsFirstAskHoldsASlotUntilReleased()
    {
        var requests = Requests();
        Assert.Equal(ServerBakeBuilderRequests.Answer.Ready, requests.Ask(new Vector2s(1, 1), () => true));
        Assert.Equal(1, requests.Count);
        requests.Release(new Vector2s(1, 1));
        Assert.Equal(0, requests.Count);
    }

    [Fact]
    public void AReleasedZoneFreesItsSlotForTheNext()
    {
        var requests = Requests();
        for (int i = 0; i < 8; i++) requests.Ask(new Vector2s(i, 0), () => false);
        // Took its data, gave up, or left the queue: released either way.
        requests.Release(new Vector2s(5, 0));
        Assert.Equal(ServerBakeBuilderRequests.Answer.Building, requests.Ask(new Vector2s(9, 0), () => false));
        // Releasing a zone that holds no slot changes nothing.
        requests.Release(new Vector2s(42, 42));
        Assert.Equal(8, requests.Count);
    }

    [Fact]
    public void ANewQueueStartsWithNoRequests()
    {
        var requests = Requests();
        for (int i = 0; i < 8; i++) requests.Ask(new Vector2s(i, 0), () => false);
        requests.Clear();
        Assert.Equal(0, requests.Count);
        Assert.Equal(ServerBakeBuilderRequests.Answer.Ready, requests.Ask(new Vector2s(9, 0), () => true));
    }

    /// <summary>The shipped cap is half the game's ready list, through the one helper.</summary>
    [Fact]
    public void TheShippedCapIsTheHelpersHalf() =>
        Assert.Equal(ServerBakeBuilderRequests.HalfTheReadyList(GameReadySlots), Requests().Max);

    /// <summary>
    /// The game's builder as the bake sees it: one thread working a FIFO list, <c>perFrame</c> builds a frame, its
    /// finished builds on a ready list until the bake takes them. Asking for a zone not built or building queues it.
    /// </summary>
    private sealed class Builder
    {
        private readonly int m_perFrame;
        private readonly Queue<Vector2s> m_toBuild = new();
        private readonly HashSet<Vector2s> m_queued = new(), m_ready = new();
        public int Asked;
        public Builder(int perFrame) => m_perFrame = perFrame;

        public bool IsReady(Vector2s zone)
        {
            if (m_ready.Contains(zone)) return true;
            if (m_queued.Add(zone)) { m_toBuild.Enqueue(zone); Asked++; }
            return false;
        }

        public void Take(Vector2s zone) => m_ready.Remove(zone);

        public void Frame()
        {
            for (int i = 0; i < m_perFrame && m_toBuild.Count > 0; i++)
            {
                Vector2s zone = m_toBuild.Dequeue();
                m_queued.Remove(zone);
                m_ready.Add(zone);
            }
        }
    }

    /// <summary>Drain a queue of <paramref name="zones"/> the way the bake does, a slice a frame through
    /// RunSlice; the frames it took, how many builds were asked for, and the most requests held at once.</summary>
    private static (int frames, int done, int asked, int most, int tries) Drain(int zones, int buildsPerFrame, int maxFrames)
    {
        var requests = Requests();
        var builder = new Builder(buildsPerFrame);
        var queue = new List<Vector2s>();
        for (int i = 0; i < zones; i++) queue.Add(new Vector2s(i, 0));
        int next = 0, frame = 0, done = 0, most = 0, tries = 0;
        while ((next < queue.Count || requests.Count > 0) && frame < maxFrames)
        {
            frame++;
            requests.RunSlice(queue, ref next, zone =>
            {
                var answer = requests.Ask(zone, () => builder.IsReady(zone));
                most = System.Math.Max(most, requests.Count);
                if (answer == ServerBakeBuilderRequests.Answer.Ready)
                {
                    builder.Take(zone);
                    requests.Release(zone);
                    done++;
                    return false;
                }
                if (answer == ServerBakeBuilderRequests.Answer.Building) tries++;
                return true;
            }, () => true);
            builder.Frame();
        }
        return (frame, done, builder.Asked, most, tries);
    }

    /// <summary>A queue of twenty zones the builder builds one a frame: the bake never has more than eight asked at
    /// once, every zone is built in the end, each build is asked for once, and a capped zone spends no try.</summary>
    [Fact]
    public void AQueueDrainsWithNeverMoreThanTheCapAsked()
    {
        var (frames, done, asked, most, tries) = Drain(20, 1, 1000);
        Assert.Equal(20, done);
        Assert.Equal(8, most);
        Assert.Equal(20, asked);
        // Each zone is asked once, then answered "building" once more a frame for every frame it waits; never more
        // tries than frames times the cap, none for a zone the cap refused.
        Assert.True(tries <= frames * 8, $"{tries} tries over {frames} frames");
        Assert.True(frames <= 20 + 2, $"{frames} frames");
    }

    /// <summary>
    /// A thousand zones (a long-explored world's first load): the frames grow with the zones, not with their square.
    /// Sending capped and building zones to the back of the queue wrote only the eight asked per pass over the queue,
    /// about N squared over 64 frames (over fifteen thousand here); worked in place, it is about one a build.
    /// </summary>
    [Fact]
    public void AThousandZoneQueueDrainsInFramesLinearInItsLength()
    {
        const int zones = 1000;
        var (frames, done, asked, most, _) = Drain(zones, 1, 100000);
        Assert.Equal(zones, done);
        Assert.Equal(zones, asked);
        Assert.Equal(8, most);
        Assert.True(frames <= zones + 2, $"{frames} frames for {zones} zones");
        // A faster builder, faster still: the cap, not the queue, bounds a frame.
        var (fast, fastDone, _, _, _) = Drain(zones, 8, 100000);
        Assert.Equal(zones, fastDone);
        Assert.True(fast <= zones / 8 + 2, $"{fast} frames for {zones} zones at eight builds a frame");
    }

    /// <summary>A zone the cap refuses is not worked at all: the slice ends before it, and it is the next one worked.</summary>
    [Fact]
    public void AFullCapEndsTheSliceBeforeTheNextZone()
    {
        var requests = Requests();
        var queue = new List<Vector2s>();
        for (int i = 0; i < 12; i++) queue.Add(new Vector2s(i, 0));
        var worked = new List<Vector2s>();
        int next = 0;
        requests.RunSlice(queue, ref next, zone =>
        {
            worked.Add(zone);
            return requests.Ask(zone, () => false) != ServerBakeBuilderRequests.Answer.Ready;
        }, () => true);
        Assert.Equal(8, worked.Count);
        Assert.Equal(8, next);
        Assert.Equal(12, queue.Count);
        // Next slice: the eight waiting ones first, in the order asked, and the queue not touched while all wait.
        worked.Clear();
        requests.RunSlice(queue, ref next, zone =>
        {
            worked.Add(zone);
            return requests.Ask(zone, () => false) != ServerBakeBuilderRequests.Answer.Ready;
        }, () => true);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, worked.ConvertAll(z => (int)z.x).ToArray());
        Assert.Equal(8, next);
    }

    /// <summary>A new append's zones join the bake's queue (ServerBakeBuilderRequests.Admit, as ServerTerrainBake.EnqueueAppend
    /// asks it): a zone the queue worked already is owed again, a completed zone still owing a new append; one still to be
    /// worked, holding a request or held for a deferral is not queued twice, and takes the new work when it is worked.</summary>
    [Fact]
    public void AWorkedZoneIsQueuedAgainForANewAppendAndAPendingOneIsNotQueuedTwice()
    {
        var requests = Requests();
        Vector2s worked = new(0, 0), pending = new(1, 0), asked = new(2, 0), deferred = new(3, 0), fresh = new(4, 0);
        var queue = new List<Vector2s> { worked, pending };
        requests.Ask(asked, () => false);
        int added = requests.Admit(queue, 1, new[] { worked, pending, asked, deferred, fresh }, zone => zone == deferred);
        Assert.Equal(2, added);
        Assert.Equal(new[] { worked, pending, worked, fresh }, queue);
    }
}
