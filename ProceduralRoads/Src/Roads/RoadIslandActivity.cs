using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace ProceduralRoads;

/// <summary>A detached observation. Slot is logical, never an OS thread identity.</summary>
public sealed class RoadIslandProgress
{
    public int Slot { get; }
    public int Island { get; }
    public long Assignment { get; }
    public bool Active { get; }
    public string Stage { get; }
    public int Processed { get; }
    public int Destinations { get; }
    public int Reached { get; }
    public double Kilometres { get; }
    public int PricesCompleted { get; }
    public int PricesTotal { get; }
    public long SearchSteps { get; }
    public int ActiveSearches { get; }
    /// <summary>Seconds since this island was claimed (not since the stage began).</summary>
    public double IslandSeconds { get; }
    /// <summary>Seconds since the island began connecting its places (pricing excluded); the time-left estimate's base.</summary>
    public double SettlingSeconds { get; }
    /// <summary>Settled places the network did not reach: no road was built to them.</summary>
    public int Unreached { get; }
    public int Bridges { get; }
    public int Staircases { get; }
    /// <summary>Prefab name of the place being connected, and its distance from the road it starts from; null before the first.</summary>
    public string? Destination { get; }
    public double DestinationMetres { get; }
    internal RoadIslandProgress(int slot, int island, long assignment, bool active, string stage,
        int processed, int destinations, int reached, double metres, int pricesCompleted, int pricesTotal,
        long searchSteps, int activeSearches, double seconds,
        int unreached = 0, int bridges = 0, int staircases = 0, string? destination = null, double destinationMetres = 0,
        double settlingSeconds = 0)
    { Slot=slot; Island=island; Assignment=assignment; Active=active; Stage=stage; Processed=processed;
      Destinations=destinations; Reached=reached; Kilometres=metres/1000; PricesCompleted=pricesCompleted;
      PricesTotal=pricesTotal; SearchSteps=searchSteps; ActiveSearches=activeSearches; IslandSeconds=seconds;
      Unreached=unreached; Bridges=bridges; Staircases=staircases; Destination=destination; DestinationMetres=destinationMetres;
      SettlingSeconds=settlingSeconds; }
}

/// <summary>Fixed storage belonging to one generation. No event queue or per-node synchronization.</summary>
internal sealed class RoadIslandActivity
{
    internal sealed class Token
    {
        private readonly RoadIslandActivity owner;
        internal readonly int Slot, Island;
        internal readonly long Epoch;
        private long nextReachSample;
        internal Token(RoadIslandActivity owner, int slot, int island, long epoch)
        { this.owner=owner; Slot=slot; Island=island; Epoch=epoch; }
        /// <summary>The place being connected belongs to the connecting stage only; any other stage forgets it.</summary>
        internal void Stage(string stage) => owner.Update(this, s => { s.Stage=stage; if (stage!="Connecting") s.Destination=null; });
        internal void Pricing(int total) => owner.Update(this, s => { s.Stage="Pricing"; s.Destination=null; s.PricesTotal=total; s.PricesCompleted=0; });
        internal void Priced() => owner.Update(this, s => s.PricesCompleted++);
        /// <summary>A place is done: built to, joined, or given up on. <paramref name="served"/> is false when the network does not reach it.</summary>
        internal void Settled(int index, bool served = true) => owner.Update(this, s =>
        {
            if (index<0 || index>=s.Settled.Length || s.Settled[index]) return;
            s.Settled[index]=true;
            if (!served) s.Unreached++;
        });
        /// <summary>The place the serial planner is connecting now, and how far it is from the road it starts from.</summary>
        internal void Connecting(string name, float metres) => owner.Update(this, s =>
        {
            s.Destination=name; s.DestinationMetres=metres;
            if (s.Working==0) s.Working=Stopwatch.GetTimestamp();
        });
        // Called only by this island's serial planner, outside the snapshot lock.
        internal bool ReachSampleDue()
        {
            long now=Stopwatch.GetTimestamp();
            if(now<nextReachSample) return false;
            nextReachSample=now+Stopwatch.Frequency/2;
            return true;
        }
        internal void Reached(int count) => owner.Update(this, s => s.Reached=count);
        /// <summary>A measurement of which places the network serves now, by index. Reached counts them all;
        /// unreached counts the settled ones it does not serve, so a place a later road passes leaves "unreached".</summary>
        internal void Measured(bool[] served) => owner.Update(this, s =>
        {
            int reached=0, unreached=0;
            for (int i=0;i<served.Length;i++)
            {
                if (served[i]) reached++;
                else if (i<s.Settled.Length && s.Settled[i]) unreached++;
            }
            s.Reached=reached; s.Unreached=unreached;
        });
        internal void Committed(float metres) => owner.Update(this, s => s.Metres+=metres);
        internal void BuiltStaircases(int count) { if (count>0) owner.Update(this, s => s.Staircases+=count); }
        internal void BuiltBridges(int count) { if (count>0) owner.Update(this, s => s.Bridges+=count); }
        internal void Search(int steps, int activeDelta) => owner.Search(this,steps,activeDelta);
        internal void Finish() => owner.Release(this);
    }
    private sealed class Slot
    {
        internal long Epoch;
        internal int Island, Reached, PricesCompleted, PricesTotal, ActiveSearches, Unreached, Bridges, Staircases;
        internal bool Active;
        internal bool[] Settled=Array.Empty<bool>();
        internal string Stage="Waiting";
        internal long Claimed, Ended, Working, SearchSteps;
        internal double Metres, DestinationMetres;
        internal string? Destination;
    }
    private readonly object gate = new();
    private readonly Slot[] slots;
    private bool closed;
    internal RoadIslandActivity(int workers, int islands)
    { slots=new Slot[Math.Min(Math.Max(0,workers),Math.Max(0,islands))]; for(int i=0;i<slots.Length;i++) slots[i]=new Slot(); }
    internal Token Claim(int island, int destinations)
    {
        lock(gate)
        {
            if(closed) throw new InvalidOperationException("Generation activity is closed.");
            for(int i=0;i<slots.Length;i++) if(!slots[i].Active)
            {
                long epoch=slots[i].Epoch+1;
                slots[i]=new Slot { Active=true, Island=island, Epoch=epoch, Settled=new bool[destinations], Stage="Preparing", Claimed=Stopwatch.GetTimestamp() };
                return new Token(this,i,island,epoch);
            }
            throw new InvalidOperationException("No generation slot available.");
        }
    }
    private void Update(Token token, Action<Slot> update)
    { lock(gate) { var s=slots[token.Slot]; if(!closed && s.Active && s.Epoch==token.Epoch && s.Island==token.Island) update(s); } }
    private void Search(Token token,int steps,int activeDelta)
    {
        lock(gate)
        {
            var s=slots[token.Slot];
            if(!closed && s.Active && s.Epoch==token.Epoch && s.Island==token.Island)
            { s.SearchSteps+=steps; s.ActiveSearches+=activeDelta; }
        }
    }
    private void Release(Token token)
    { Update(token,s=> { s.Stage="Finished"; s.Ended=Stopwatch.GetTimestamp(); s.Active=false; }); }
    internal void Close() { lock(gate) closed=true; }
    internal ReadOnlyCollection<RoadIslandProgress> Snapshot()
    {
        lock(gate)
        {
            var result=new RoadIslandProgress[slots.Length];
            for(int i=0;i<slots.Length;i++)
            {
                var s=slots[i]; int processed=0; foreach(bool done in s.Settled) if(done) processed++;
                result[i]=new RoadIslandProgress(i,s.Island,s.Epoch,s.Active,s.Stage,processed,s.Settled.Length,s.Reached,
                    s.Metres,s.PricesCompleted,s.PricesTotal,s.SearchSteps,s.ActiveSearches,
                    s.Claimed==0?0:((s.Ended!=0?s.Ended:Stopwatch.GetTimestamp())-s.Claimed)/(double)Stopwatch.Frequency,
                    s.Unreached,s.Bridges,s.Staircases,s.Destination,s.DestinationMetres,
                    s.Working==0?0:((s.Ended!=0?s.Ended:Stopwatch.GetTimestamp())-s.Working)/(double)Stopwatch.Frequency);
            }
            return Array.AsReadOnly(result);
        }
    }
}

/// <summary>One search's local batch; only every 256 iterations touches shared counters.</summary>
internal sealed class RoadSearchActivity : IDisposable
{
    private readonly RoadIslandActivity.Token? token;
    private int pending;
    internal RoadSearchActivity(RoadIslandActivity.Token? token) { this.token=token; token?.Search(0,1); }
    internal void Step() { if(token!=null && ++pending==256) { token.Search(pending,0); pending=0; } }
    public void Dispose() { token?.Search(pending,-1); }
}

internal sealed class RoadFittingActivity : IDisposable
{
    private readonly RoadIslandActivity.Token? token;
    internal RoadFittingActivity(RoadIslandActivity.Token? token) { this.token=token; token?.Stage("Fitting"); }
    public void Dispose() => token?.Stage("Connecting");
}
