using System.Text.Json;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

public sealed record PersistentSample(int ZoneOffset, float X, float Z, float Before, float Expected, float Height, float ColliderHeight)
{
    public bool Passed => float.IsFinite(Height) && float.IsFinite(ColliderHeight) && Math.Abs(Height-Expected)<=.02f && Math.Abs(ColliderHeight-Expected)<=.02f;
}
public static class PersistentTerrainScenario
{
    public const string PaintProfileVariable="ROADS_TEST_PAINT_PROFILE";
    public const string DirtFade="dirt-fade";
    // Accepted plan values: absent (paved, width 4) or exactly dirt-fade (width 8); a key differing only in case is refused.
    public static int WidthFor(ServerRunPlan plan)=>plan.EnvironmentChoice(PaintProfileVariable,DirtFade)==DirtFade ? 8 : 4;
    public static string ProfileName(int width)=>width switch{4=>"paved",8=>DirtFade,_=>throw new ArgumentException("Unsupported fixture width.")};
    // Independent analytic oracle for deliberately constant width-4/8 roads:
    // flat through half-width, half one metre beyond, zero two metres beyond;
    // delta limited to +/-8 m. One metre into the 2 m smoothstep release is
    // its midpoint, 1-(3*.5^2-2*.5^3) = .5. The neighbouring road points, 2 m
    // along the road, are farther away and blend less, so they do not raise it.
    public static int[] Distances(int width)=>width switch{4=>new[]{0,2,3,4,6},8=>new[]{0,2,3,4,5,6},_=>throw new ArgumentException("Unsupported fixture width.")};
    public static float Expected(float before, float target, int distance, int width = 4)
    {
        if(!float.IsFinite(before)||!float.IsFinite(target)||distance<0) throw new ArgumentException("Invalid fixture input.");
        if(width!=4 && width!=8) throw new ArgumentException("Unsupported fixture width.");
        float fraction=distance<=width/2 ? 1 : distance==width/2+1 ? .5f : 0;
        return before+Math.Clamp((target-before)*fraction,-8,8);
    }
    // The plan's profile decides the width; the adapter's report must agree.
    public static IReadOnlyList<PersistentSample> Compare(JsonElement data, int expectedWidth)
    {
        string profile=ProfileName(expectedWidth);
        if(data.GetProperty("source").GetString()!="natural-heightmap-road-fixture" || !data.GetProperty("complete").GetBoolean() || data.GetProperty("margin").GetInt32()!=2)
            throw new InvalidOperationException("Wrong persistent fixture.");
        int width=data.GetProperty("width").GetInt32();
        string? reported=data.TryGetProperty("paintProfile",out var p) && p.ValueKind==JsonValueKind.String ? p.GetString() : null;
        if(width!=expectedWidth || reported!=profile)
            throw new InvalidOperationException($"Adapter reported width {width} ({reported ?? "no profile"}); the plan requested {profile}, width {expectedWidth}.");
        float x=data.GetProperty("x").GetSingle(),z=data.GetProperty("z").GetSingle(),target=data.GetProperty("target").GetSingle();
        if(!float.IsFinite(x)||!float.IsFinite(z)||Math.Abs(x)>20000||Math.Abs(z)>20000) throw new InvalidOperationException("Invalid fixture origin.");
        var wanted=new HashSet<(int,float,float)>();
        for(int zone=0;zone<2;zone++) foreach(int dx in zone==0?new[]{-8,0}:new[]{0,8}) foreach(int dz in Distances(expectedWidth)) wanted.Add((zone,x+dx,z+dz));
        var rows=new List<PersistentSample>();
        foreach(var row in Coverage.Exact(wanted,data.GetProperty("samples").EnumerateArray(),r=>(r.GetProperty("zoneOffset").GetInt32(),r.GetProperty("x").GetSingle(),r.GetProperty("z").GetSingle())))
        {
            int zone=row.GetProperty("zoneOffset").GetInt32(),distance=row.GetProperty("distance").GetInt32();
            float sx=row.GetProperty("x").GetSingle(),sz=row.GetProperty("z").GetSingle(),before=row.GetProperty("before").GetSingle();
            if(sz-z!=distance) throw new InvalidOperationException("Shifted sample identity.");
            rows.Add(new(zone,sx,sz,before,Expected(before,target,distance,expectedWidth),row.GetProperty("height").GetSingle(),row.GetProperty("colliderHeight").GetSingle()));
        }
        foreach(var pair in rows.GroupBy(r=>(r.X,r.Z)))
            if(pair.Max(r=>r.Before)-pair.Min(r=>r.Before)>.02f) throw new InvalidOperationException("Boundary input copies disagree.");
        if(!rows.Any(r=>Math.Abs(r.Expected-r.Before)>.5f)) throw new InvalidOperationException("Fixture has no discriminating terrain change.");
        return rows;
    }
    public static IReadOnlyList<PaintExpectation> ComparePaint(JsonElement data)
    {
        if(data.GetProperty("paintInput").GetString()!="saved-explicit-rgba") throw new InvalidOperationException("Paint needs a persistent declared baseline.");
        float x=data.GetProperty("x").GetSingle(), z=data.GetProperty("z").GetSingle();
        int width=data.TryGetProperty("width",out var w)?w.GetInt32():4;
        if(width!=4 && width!=8) throw new InvalidOperationException("Unsupported paint fixture width.");
        var wanted=new HashSet<(int,float,float)>();
        for(int zone=0;zone<2;zone++) foreach(int dx in zone==0?new[]{-8,-2}:new[]{2,8}) foreach(int dz in width==8?new[]{0,2,3,4}:new[]{0,1,2,4}) wanted.Add((zone,x+dx,z+dz));
        var expected=new List<PaintExpectation>();
        bool changed=false;
        foreach(var row in Coverage.Exact(wanted,data.GetProperty("paintSamples").EnumerateArray(),r=>(r.GetProperty("zoneOffset").GetInt32(),r.GetProperty("x").GetSingle(),r.GetProperty("z").GetSingle())))
        {
            int zone=row.GetProperty("zoneOffset").GetInt32(),distance=row.GetProperty("distance").GetInt32();
            float sx=row.GetProperty("x").GetSingle(),sz=row.GetProperty("z").GetSingle();
            if(sz-z!=distance) throw new InvalidOperationException("Shifted paint sample identity.");
            var before=row.GetProperty("before").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            var actual=row.GetProperty("paint").EnumerateArray().Select(v=>v.GetSingle()).ToArray();
            if(before.Length!=4 || actual.Length!=4 || before.Concat(actual).Any(v=>!float.IsFinite(v)||v<0||v>1)) throw new InvalidOperationException("Invalid RGBA input.");
            // Independent of RoadSurface/paint helpers: outside the entire
            // 1000 +/-100 m handover plus its half-width, colour is unambiguous.
            float radius=MathF.Sqrt(sx*sx+sz*sz);
            if(radius>=895 && radius<=1105) throw new InvalidOperationException("Fixture lies in the surface transition ring.");
            // Width 8: fade at distance 3 is 1-smoothstep((3-2.4)/(3.4-2.4)) = .352.
            // Neighbouring 2m-spaced points are sqrt(13)>3.4m away, so only one paints this texel.
            float strength=width==8 ? (distance<=2?1f:distance==3?.352f:0f) : (distance<=1?1f:0f);
            var colour=new[]{radius<895?1f:0f,0f,radius>1105?1f:0f};
            float[] target={before[0]+(colour[0]-before[0])*strength,before[1]+(colour[1]-before[1])*strength,before[2]+(colour[2]-before[2])*strength,before[3]};
            if(target.Zip(actual).Any(v=>Math.Abs(v.First-v.Second)>.01f)) throw new InvalidOperationException("Native paint differs from declared core/verge expectation.");
            changed |= target.Zip(before).Any(v=>Math.Abs(v.First-v.Second)>.1f);
            expected.Add(new PaintExpectation((int)sx,(int)sz,target[0],target[1],target[2],target[3]));
        }
        if(!changed) throw new InvalidOperationException("Non-discriminating paint fixture.");
        return expected;
    }
    // Valheim's sea level. A player on ground less than this margin above it can stand in water (28 Sep: swimming at
    // y=30 on a dirt fixture), which says nothing about the road.
    public const float SeaLevel=30f, DryMargin=1.5f;
    /// <summary>The highest road-core vertex (on the road's own line), if it is genuinely dry; otherwise the fixture cannot test grounding.</summary>
    public static HeightExpectation DrySupport(IEnumerable<PersistentSample> rows, float roadZ)
    {
        var core=rows.Where(r=>r.Z==roadZ).OrderByDescending(r=>r.Expected).ThenBy(r=>r.X).FirstOrDefault()
            ?? throw new InvalidOperationException("The fixture has no road-core sample to stand on.");
        if(core.Expected<SeaLevel+DryMargin)
            throw new InvalidOperationException($"No genuinely dry support point: the highest road-core vertex is {core.Expected:F2} m, less than {DryMargin} m above the sea at {SeaLevel} m. Prepare a drier fixture.");
        return new HeightExpectation(core.X,core.Z,core.Expected);
    }
    public static void Prepare(GameActor server, ScenarioReport report, string output, int expectedWidth)
    {
        var data=server.Invoke(server.RequireCapability("roads.testing/terrain-persist"));
        File.WriteAllText(Path.Combine(output,"persistent-inputs.json"),data.GetRawText());
        var rows=Compare(data,expectedWidth);
        File.WriteAllText(Path.Combine(output,"persistent-residuals.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        report.Step("native inputs plus analytic road profile agree",()=>{if(rows.Any(r=>!r.Passed)) throw new InvalidOperationException("Persistent fixture differs from its analytic expectation.");});
        var samples=rows.GroupBy(r=>(r.X,r.Z)).Select(g=>new HeightExpectation(g.Key.X,g.Key.Z,g.First().Expected)).ToArray();
        HeightExpectation? support=null;
        report.Step("a genuinely dry support point on the road core",()=>support=DrySupport(rows,data.GetProperty("z").GetSingle()));
        new SurfacePlan{ExpectedFrom="Native pre-write heightmap vertices plus declared constant-width target, half-strength one metre beyond its half-width and +/-8m clamp; persistent-inputs.json",Tolerance=.05f,Samples=[..samples],Support=support}.Write(Path.Combine(output,"client-height-plan.json"));
        report.Step("native paint matches independent core and unpainted verge",()=>
        {
            var paint=ComparePaint(data);
            new PaintPlan{ExpectedFrom="Native pre-write RGBA, declared width-4 or width-8 core/fade/verge, outside transition ring; alpha preserved. persistent-inputs.json",Tolerance=.01f,Samples=[..paint]}.Write(Path.Combine(output,"client-paint-plan.json"));
        });
        report.Step("confirmed fixture save",()=>server.SaveConfirmed());
    }
}
