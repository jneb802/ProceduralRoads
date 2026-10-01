using System.Globalization;
using System.Text.Json;
using ProceduralRoads;
using ProceduralRoads.Tests;
using UnityEngine;

// A terrain stress screen, not a replay of generated networks or placed POIs.
// Each family contributes its four largest cardinal arrival-height mismatches.
// Calling production Improve/PlanRoadPath avoids duplicating routing decisions.
if (args.Length != 3) throw new ArgumentException("cells8.csv locations.csv output.json");
if (File.Exists(args[2])) throw new IOException("Refusing to overwrite an audit");
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var world = new CsvWorld().Load(args[0]);
WorldGenerator.instance = world;
RoadGrade.Configured = 0.35f;
RoadSpatialGrid.Clear();
var locations = File.ReadLines(args[1]).Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => {
    var c = s.Trim().Split(',');
    if (c.Length != 4) throw new InvalidDataException(s);
    return new Site(c[0], new Vector2(float.Parse(c[1]), float.Parse(c[2])), float.Parse(c[3]));
}).ToArray();
RoadSiteProtection.Set(locations.Select(s => new RoadSiteProtection.Footprint(s.Centre, s.Radius)));
string? Family(string name) => name.StartsWith("MountainCave") ? "frost cave" :
    name.StartsWith("TrollCave") ? "troll cave" : name.StartsWith("TarPit") ? "tar pit" :
    name.Contains("Tower", StringComparison.OrdinalIgnoreCase) ? "tower" :
    name.Contains("Crypt", StringComparison.OrdinalIgnoreCase) || name.Contains("Burial", StringComparison.OrdinalIgnoreCase) ? "dungeon" :
    name.Contains("Village", StringComparison.OrdinalIgnoreCase) || name.Contains("Farm", StringComparison.OrdinalIgnoreCase) ? "settlement" : null;
float Ground(Vector2 p) => world.GetHeight(p.x, p.y);
var candidates = new List<(Site Site, string Family, Vector2 Direction, float Mismatch)>();
int excludedBiome = 0, excludedSizeOrWet = 0;
foreach (var site in locations)
{
    string? family = Family(site.Name); if (family == null) continue;
    var biome = world.GetBiome(site.Centre.x, site.Centre.y);
    if (biome == Heightmap.Biome.AshLands || biome == Heightmap.Biome.DeepNorth) { excludedBiome++; continue; }
    float radius = site.Radius + 4;
    if (site.Radius <= 0 || radius > 64 || Ground(site.Centre) < 32) { excludedSizeOrWet++; continue; }
    var directions = new[] { new Vector2(1,0), new Vector2(0,1), new Vector2(-1,0), new Vector2(0,-1) };
    var worst = directions.Select(d => (Direction:d, Mismatch:Mathf.Abs(Ground(site.Centre+d*radius)-Ground(site.Centre))))
        .OrderByDescending(d => d.Mismatch).First();
    candidates.Add((site, family, worst.Direction, worst.Mismatch));
}
var chosen = candidates.GroupBy(c => c.Family).OrderBy(g => g.Key)
    .SelectMany(g => g.OrderByDescending(c => c.Mismatch).ThenBy(c=>c.Site.Name).ThenBy(c=>c.Site.Centre.x).ThenBy(c=>c.Site.Centre.y).Take(4)).ToArray();
var rows = new List<object>();
int changed = 0, invalid = 0;
foreach (var c in chosen)
{
    var site = c.Site; float radius = site.Radius + 4, desired = Ground(site.Centre);
    var path = new List<Vector2>();
    for (int step = 24; step >= 0; step--) path.Add(site.Centre+c.Direction*(radius+step*8));
    // Same natural-arrival policy as production, but reference height is only
    // unmodified centre ground: dumps contain no saved root/authored modifiers.
    float? Target(Vector2 p) => Mathf.Abs(Ground(p)-desired)<=1.5f ? Ground(p) : null;
    long heightQueries = world.HeightQueries, biomeQueries = world.BiomeQueries;
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var better = RoadSiteApproach.Improve(path,site.Centre,radius,4,world,false,Target,Ground,desired);
    watch.Stop();
    long approachBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    long approachHeights = world.HeightQueries - heightQueries, approachBiomes = world.BiomeQueries - biomeQueries;
    // Exact control-point identity, not a rounded length or destination count.
    using var routeBytes = new MemoryStream();
    using (var writer = new BinaryWriter(routeBytes, System.Text.Encoding.UTF8, true))
        foreach (var point in better) { writer.Write(point.x); writer.Write(point.y); }
    string routeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(routeBytes.ToArray()));
    bool moved = !ReferenceEquals(path,better); if (moved) changed++;
    int originalJoin = path.Count - 1; float tailLength = 0;
    while (originalJoin > 0 && tailLength < RoadSiteApproach.Reach)
    { tailLength += Vector2.Distance(path[originalJoin],path[originalJoin-1]); originalJoin--; }
    Vector2 originalAnchor = path[originalJoin];
    var before = Measure(path); var after = Measure(better);
    bool? replacementChecksPassed = moved
        ? after.Profile && after.Grade<=0.3502f && after.BlockedSegments==0 && after.ArrivalMismatch<=1.501f
        : null;
    if (replacementChecksPassed == false) invalid++;
    rows.Add(new {name=site.Name,family=c.Family,x=site.Centre.x,z=site.Centre.y,exteriorRadius=site.Radius,
        referenceHeight=desired,initialDirection=new[]{c.Direction.x,c.Direction.y},changed=moved,replacementChecksPassed,
        seconds=watch.Elapsed.TotalSeconds,approachBytes,approachHeights,approachBiomes,routeHash,before,after});
    Console.WriteLine($"{site.Name} ({site.Centre.x},{site.Centre.y}): changed={moved}, mismatch {before.ArrivalMismatch:F2}->{after.ArrivalMismatch:F2}, replacementChecks={replacementChecksPassed}");

    Metrics Measure(List<Vector2> points)
    {
        // Measure only the replaced tail from the SAME original join anchor.
        Vector2 anchor = originalAnchor;
        int join = points.FindIndex(p => p.Equals(anchor));
        if (join < 0) throw new InvalidOperationException("Original join anchor lost");
        var tail = points.GetRange(join,points.Count-join);
        var plan = RoadSpatialGrid.PlanRoadPath(tail,4,world,Ground(anchor),Target(tail[tail.Count-1]));
        float mismatch = Mathf.Abs(Ground(points[points.Count-1])-desired);
        if (plan == null) return new(false,mismatch,0,0,0,0,0);
        double total = 0; float max = 0; int blocked = 0;
        for (int i=0;i<plan.Points.Count;i++)
        {
            var p=plan.Points[i];
            var tangent = plan.Points[Math.Min(i+1,plan.Points.Count-1)]-plan.Points[Math.Max(0,i-1)];
            var side = new Vector2(-tangent.y,tangent.x).normalized*2;
            foreach(var sample in new[]{p-side,p,p+side})
            { float delta=Mathf.Abs(plan.Heights[i]-Ground(sample));total+=delta;max=Mathf.Max(max,delta); }
            if (RoadSiteProtection.BlocksSegment(i==0?p:plan.Points[i-1],p,4,null,null)) blocked++;
        }
        return new(true,mismatch,RoadGrade.SteepestStep(plan.Points,plan.Heights),
            (float)(total/(plan.Points.Count*3)),max,plan.TotalLength,blocked);
    }
}
File.WriteAllText(args[2],JsonSerializer.Serialize(new {
    terrain=world.Describe(),approximations=world.Approximations,
    kind="hypothetical radial approaches on procedural terrain; not actual network or authored POI replay",
    locations=locations.Length,candidateSites=candidates.Count,excludedBiome,excludedSizeOrWet,
    tested=rows.Count,changed,invalid,rows
},new JsonSerializerOptions{WriteIndented=true}));
if (invalid>0) Environment.ExitCode=1;
record Site(string Name,Vector2 Centre,float Radius);
record Metrics(bool Profile,float ArrivalMismatch,float Grade,float MeanEarthwork,float WorstEarthwork,float Length,int BlockedSegments);
