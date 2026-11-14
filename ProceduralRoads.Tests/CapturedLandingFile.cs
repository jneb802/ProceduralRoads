using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>Numeric native-input adapter. Records, terrain interpolation and planning retain their production owners.</summary>
internal sealed class CapturedLandingFile
{
    internal PlacedLocationRecord Record = null!;
    internal LocationApproachRequest Request = null!;
    internal Func<float, float, float> Ground = null!, Natural = null!;
    internal int Seed;
    internal string InputHash = "";
    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static Vector2 V(string s) { var p=s.Split(','); return new Vector2(F(p[0]),F(p[1])); }

    /// <summary>The captured request with its road cut <paramref name="back"/> metres back along its arrival, as
    /// RoadNetworkGenerator.PlanEntry cuts a road back for a staircase that cannot start at its end: the road's end there, at
    /// its placed ground, its tail ending there.</summary>
    internal LocationApproachRequest CutBack(float back)
    {
        LocationApproachRequest q=Request;
        Vector2 cut=q.RoadEnd-StairLayout.Forward(q.Arriving)*back;
        return new LocationApproachRequest { Centre=q.Centre,RoadEnd=cut,RoadHeight=Ground(cut.x,cut.y),Arriving=q.Arriving,Reach=q.Reach,
            TotalExpansions=q.TotalExpansions,Stall=q.Stall,Leg=q.Leg,RoadTail=new System.Collections.Generic.List<Vector2>{q.RoadTail[0],cut} };
    }

    internal static CapturedLandingFile Tower()
    {
        string[] lines=File.ReadAllLines(Path.Combine(AppContext.BaseDirectory,"Fixtures","stairs-tower-actual.txt"));
        var r=PlacedLocations.Read(string.Join("\n",lines.Where(l=>new[]{"site,","op,","rock,","top,"}.Any(p=>l.StartsWith(p,StringComparison.Ordinal))))).Single();
        var rows=lines.Where(l=>l.StartsWith("input-ground\t",StringComparison.Ordinal)).Select(l=>l.Split('\t')).ToArray();
        if(rows.Length!=r.W*r.H || rows.Length!=8100)throw new FormatException("Incomplete captured Tower grid");
        var placed=new float[rows.Length];var natural=new float[rows.Length];
        for(int n=0;n<rows.Length;n++)
        {
            if(I(rows[n][1])!=r.X0+n%r.W || I(rows[n][2])!=r.Z0+n/r.W)throw new FormatException("Captured vertex order mismatch");
            natural[n]=F(rows[n][3]);placed[n]=F(rows[n][4]);
        }
        Func<float,float,float> Checked(float[] grid)
        {
            var sample=PlacedLocationGround.Sampler(grid,r.X0,r.Z0,r.W,r.H);
            return (x,z)=>{float h=sample(x,z);return h<PlacedLocationGround.OffGrid?h:throw new InvalidOperationException("Uncaptured ground; no invented fallback");};
        }
        string[] req=lines.Single(l=>l.StartsWith("request\t")).Split('\t');
        if(req[15]!="none")throw new FormatException("Unsupported captured continuation");
        return new CapturedLandingFile
        {
            Record=r,Ground=Checked(placed),Natural=Checked(natural),Seed=I(lines.Single(l=>l.StartsWith("seed\t")).Split('\t')[1]),
            InputHash=lines.Single(l=>l.StartsWith("input-sha256\t")).Split('\t')[1],
            Request=new LocationApproachRequest { Centre=r.Centre,RoadEnd=V(req[1]),RoadHeight=F(req[3]),Arriving=F(req[5]),Reach=F(req[7]),
                TotalExpansions=I(req[9]),Stall=I(req[11]),Leg=I(req[13]),RoadTail=lines.Single(l=>l.StartsWith("tail\t")).Substring(5).Split(';').Select(V).ToList() },
        };
    }
}
