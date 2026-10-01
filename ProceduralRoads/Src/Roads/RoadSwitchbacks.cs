using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Give reversals a real turning radius and a gentle landing, or refuse them.
/// No terrain is changed here. The caller still checks water, sites and the final profile.</summary>
public static class RoadSwitchbacks
{
    public const float LandingGrade = 0.10f;

    public static bool Shape(List<Vector2> input, float width,
        out List<Vector2>? shaped, out List<bool>? landing, Func<Vector2, float>? heightAt = null)
    {
        shaped = null; landing = null;
        var p = new List<Vector2>();
        foreach (var v in input)
        {
            if (p.Count > 0 && Vector2.Distance(p[p.Count-1],v)<0.01f) continue;
            while (p.Count > 1)
            {
                var a=(p[p.Count-1]-p[p.Count-2]).normalized;
                var b=(v-p[p.Count-1]).normalized;
                if (a.x*b.x+a.y*b.y<0.9999f) break;
                p.RemoveAt(p.Count-1);
            }
            p.Add(v);
        }
        // Snapping a grid route to an exact bank/POI can leave one short
        // overshoot at either end. Ignore that spur when identifying climbing
        // switchbacks. If there is no real reversal, preserve the old smoothing.
        ClipEnd(p, width * 2);
        p.Reverse(); ClipEnd(p, width * 2); p.Reverse();
        if (p.Count<3) return true;
        int n=p.Count;
        var angle=new float[n]; var mark=new bool[n]; bool any=false;
        for (int i=1;i<n-1;i++)
        {
            var a=(p[i]-p[i-1]).normalized; var b=(p[i+1]-p[i]).normalized;
            angle[i]=(float)Math.Atan2(a.x*b.y-a.y*b.x,a.x*b.x+a.y*b.y);
        }
        // A V, or several same-direction bends making a compact U.
        for (int i=1;i<n-1;i++)
        {
            float turn=angle[i],distance=0;
            for (int j=i;j<n-1;j++)
            {
                if (j>i)
                {
                    distance+=Vector2.Distance(p[j-1],p[j]);
                    if (distance>width*8 || angle[j]*turn<=0) break;
                    turn+=angle[j];
                }
                if (Math.Abs(turn)<Math.PI*0.60) continue;
                if (heightAt != null)
                {
                    float low = float.PositiveInfinity, high = float.NegativeInfinity;
                    for (int k=i-1;k<=j+1;k++)
                    { float h=heightAt(p[k]); low=Mathf.Min(low,h); high=Mathf.Max(high,h); }
                    // A flat bend cannot make the climbing-leg interference
                    // this rule addresses. Preserve its existing sharing geometry.
                    if (high-low<=0.5f) break;
                }
                for(int k=i;k<=j;k++) mark[k]=true;
                any=true;break;
            }
        }
        if (!any) return true;
        float radius=width+RoadConstants.TerrainBlendMargin;
        var trim=new float[n];
        for(int i=1;i<n-1;i++) if(mark[i])
        {
            if(Math.Abs(angle[i])>Math.PI-0.02) return false;
            trim[i]=radius*(float)Math.Tan(Math.Abs(angle[i])*0.5);
        }
        // Adjacent turns must not consume the same length of road.
        for(int i=0;i<n-1;i++)
            if(trim[i]+trim[i+1]>Vector2.Distance(p[i],p[i+1])-0.25f) return false;
        var output=new List<Vector2>(); var flags=new List<bool>();
        void Add(Vector2 v,bool flat)
        {
            if(output.Count>0 && Vector2.Distance(output[output.Count-1],v)<0.001f)
            { flags[flags.Count-1]|=flat;return; }
            output.Add(v);flags.Add(flat);
        }
        float step=Mathf.Max(0.25f,width/4);
        void Line(Vector2 end)
        {
            if(output.Count==0) {Add(end,false);return;}
            var from=output[output.Count-1];int steps=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(from,end)/step));
            for(int k=1;k<=steps;k++) Add(from+(end-from)*(k/(float)steps),false);
        }
        Add(p[0],false);
        for(int i=1;i<n-1;i++)
        {
            if(!mark[i]) {Line(p[i]);continue;}
            var incoming=(p[i]-p[i-1]).normalized;var outgoing=(p[i+1]-p[i]).normalized;
            var begin=p[i]-incoming*trim[i];var end=p[i]+outgoing*trim[i];
            float sign=angle[i]<0?-1:1;
            var centre=begin+new Vector2(-incoming.y,incoming.x)*(sign*radius);
            Line(begin);flags[flags.Count-1]=true;
            float start=(float)Math.Atan2(begin.y-centre.y,begin.x-centre.x);
            int count=Mathf.Max(1,Mathf.CeilToInt(radius*Mathf.Abs(angle[i])/step));
            for(int k=1;k<count;k++)
            {float a=start+angle[i]*k/count;Add(centre+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,true);}
            Add(end,true);
        }
        Line(p[n-1]);shaped=output;landing=flags;return true;
    }

    private static bool ClipEnd(List<Vector2> points, float maximumSpur)
    {
        if (points.Count < 4) return false;
        int end = points.Count - 1;
        var incoming = points[end - 1] - points[end - 2];
        var last = points[end] - points[end - 1];
        if (last.magnitude > maximumSpur || incoming.x * last.x + incoming.y * last.y >= 0)
            return false;
        points.RemoveAt(end - 1);
        return true;
    }

    /// <summary>Non-neighbouring legs at different elevations must not share
    /// terrain blend footprints. Same-level junctions are deliberately allowed.</summary>
    public static bool Separated(List<Vector2> points, List<float> heights, float width)
    {
        float reach=width+2*RoadConstants.TerrainBlendMargin;
        var bins=new Dictionary<Vector2i,List<int>>();
        var distance=new float[points.Count];
        for(int i=0;i<points.Count;i++)
        {
            if(i>0) distance[i]=distance[i-1]+Vector2.Distance(points[i-1],points[i]);
            var cell=new Vector2i(Mathf.FloorToInt(points[i].x/reach),Mathf.FloorToInt(points[i].y/reach));
            for(int z=-1;z<=1;z++) for(int x=-1;x<=1;x++)
                if(bins.TryGetValue(new Vector2i(cell.x+x,cell.y+z),out var list))
                    foreach(int j in list)
                    {
                        if(distance[i]-distance[j]<reach*2) continue;
                        if(Vector2.Distance(points[i],points[j])<reach && Mathf.Abs(heights[i]-heights[j])>0.5f)
                            return false;
                    }
            if(!bins.TryGetValue(cell,out var bucket)) bins[cell]=bucket=new List<int>();
            bucket.Add(i);
        }
        return true;
    }
}
