using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Give reversals a real turning radius and a gentle landing, or refuse them.
/// No terrain is changed here. The caller still checks water, sites and the final profile.</summary>
public static class RoadSwitchbacks
{
    public const float LandingGrade = 0.10f;

    /// <summary>
    /// Staircase turns: tight turns with a proper landing, like a staircase
    /// with one flight up and one down. The curved turn needs a 6 m radius
    /// (road width plus blend margin), so on a 127 degree zigzag turn it takes
    /// 12 m of each leg, and the search's 18 m knight legs cannot hold two
    /// turns: "turns too close to fit their curves" was the largest single
    /// refusal (291 measured, 588 once roads could contour). A staircase turn
    /// is tight (<see cref="StairRadius"/>, a 4 m landing) and FLAT: the
    /// landing runs <see cref="StairLanding"/> metres along both legs at
    /// <see cref="StairLandingGrade"/>, so where the legs are close they are at
    /// nearly the same height. Settable for tests.
    /// </summary>
    internal static bool StairTurns = true;
    internal static float StairRadius = 2f;
    internal static float StairLanding = 6f;
    internal static float StairLandingGrade = 0.05f;

    /// <summary>
    /// Round every ordinary bend with a circular arc of up to this radius, so a
    /// road flows through its waypoints instead of running straight - corner -
    /// straight. Switchbacks keep their tight turns. Each arc takes at most
    /// 45% of the legs beside it. Without it a contouring road reads as a
    /// series of 8 m straights with corners. Settable for tests; 0 is no
    /// rounding.
    /// </summary>
    internal static float BendRadius = 24f;

    /// <summary>Which points of the last Shape on this thread lie on a turn or
    /// bend arc, for the water and site checks on new geometry.</summary>
    [ThreadStatic] public static List<bool>? LastCurve;

    /// <summary>Set by planning for one retry: shape without bend arcs, so a
    /// road whose rounding fails a check keeps its corners instead of being
    /// refused.</summary>
    [ThreadStatic] public static bool SuppressBends;

    /// <summary>Set by planning for one retry (see <see cref="Fallback"/>): shape
    /// with no switchbacks at all, so a U-turn keeps the search's own corner.</summary>
    [ThreadStatic] public static bool SuppressSwitchbacks;

    /// <summary>
    /// A road refused because a switchback's curve would run into water or
    /// cross a site is planned once more without switchback shaping, and kept
    /// if its legs do not interfere at different heights. Measured: about 150
    /// roads a run were refused for a curve in water without it, 3 with it.
    /// Settable for tests.
    /// </summary>
    internal static bool Fallback = true;

    /// <summary>
    /// Staircase-tight switchbacks: a 4 m landing with one 2 m leg going up and
    /// one coming down beside it. The search's grid cannot put a switchback's
    /// legs closer than 8 m (two 90 degree steps), so shaping builds it: the
    /// two grid corners of a U-turn are replaced by a flat landing
    /// <see cref="TightSpacing"/> between leg centres, the legs narrowed to
    /// <see cref="TightWidth"/> there, and each leg widening and spreading back
    /// to its full width and the grid spacing over <see cref="TightTaper"/>.
    /// The separation rule is checked per point with these widths; planning
    /// falls back to the ordinary turn when it cannot be met. Settable for tests.
    /// </summary>
    internal static bool TightSwitchbacks = true;
    internal static float TightSpacing = 2f;
    internal static float TightWidth = 2f;
    internal static float TightTaper = 12f;


    /// <summary>Per-point road widths from the last Shape on this thread, when
    /// a tight switchback narrowed the road; null when the width is uniform.</summary>
    [ThreadStatic] public static List<float>? LastWidths;


    /// <summary>The landing grade in force: the staircase's when stair turns are on.</summary>
    public static float EffectiveLandingGrade(bool? stair = null) => (stair ?? StairTurns) ? StairLandingGrade : LandingGrade;

    /// <summary>Why the last Shape on this thread refused, for the refusal
    /// counts in the generation summary.</summary>
    [ThreadStatic] public static string? LastRefusal;

    public static bool Shape(List<Vector2> input, float width,
        out List<Vector2>? shaped, out List<bool>? landing, Func<Vector2, float>? heightAt = null, bool? stair = null, float? bend = null, bool? tight = null)
    {
        bool stairs = stair ?? StairTurns;
        float bendRadius = bend ?? (SuppressBends ? 0f : BendRadius);
        bool tightTurns = tight ?? TightSwitchbacks;
        LastRefusal = null; LastCurve = null; LastWidths = null;
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
        for (int i=1;i<n-1 && !SuppressSwitchbacks;i++)
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
        if (!any && bendRadius <= 0f) return true;
        float radius=stairs ? StairRadius : width+RoadConstants.TerrainBlendMargin;
        var trim=new float[n]; var rad=new float[n]; var bendAt=new bool[n];
        for(int i=1;i<n-1;i++) if(mark[i])
        {
            if(Math.Abs(angle[i])>Math.PI-0.02) { LastRefusal="switchback: a hairpin reverses in place"; return false; }
            trim[i]=radius*(float)Math.Tan(Math.Abs(angle[i])*0.5);
            rad[i]=radius;
        }
        // Tight staircase switchbacks: a run of marked corners making a U.
        var tightStart=new int[n]; for(int k=0;k<n;k++) tightStart[k]=-1;
        var tightIn=new Dictionary<int,(Vector2 pin,Vector2 l1,Vector2 l2,Vector2 pout,int end)>();
        if (tightTurns)
            for(int a=1;a<n-1;a++)
            {
                if(!mark[a] || (a>1 && mark[a-1])) continue;
                int b=a; while(b+1<n-1 && mark[b+1]) b++;
                float sum=0; for(int k=a;k<=b;k++) sum+=angle[k];
                float lateral=Vector2.Distance(p[a],p[b]);
                if(Math.Abs(sum)<Math.PI*150f/180f || Math.Abs(sum)>Math.PI*210f/180f || lateral<TightSpacing+0.5f) continue;
                var dIn=(p[a]-p[a-1]).normalized; var dOut=(p[b+1]-p[b]).normalized;
                float inRoom=Vector2.Distance(p[a-1],p[a])-(a-1>0?trim[a-1]:0f)-0.25f;
                float outRoom=Vector2.Distance(p[b],p[b+1])-(b+1<n-1?trim[b+1]:0f)-0.25f;
                float tIn=Mathf.Min(TightTaper,0.9f*inRoom), tOut=Mathf.Min(TightTaper,0.9f*outRoom);
                if(tIn<4f || tOut<4f) continue;
                var mid=(p[a]+p[b])*0.5f; var u=(p[b]-p[a]).normalized;
                tightIn[a]=(p[a]-dIn*tIn, mid-u*(TightSpacing*0.5f), mid+u*(TightSpacing*0.5f), p[b]+dOut*tOut, b);
                for(int k=a;k<=b;k++) { tightStart[k]=a; trim[k]=0f; }
                trim[a]=tIn; trim[b]=tOut;
            }
        if (bendRadius > 0f)
            for(int i=1;i<n-1;i++)
            {
                if(mark[i] || tightStart[i]>=0 || Math.Abs(angle[i])<0.01f || Math.Abs(angle[i])>Math.PI-0.02) continue;
                // Share each leg with the bend or turn at its other end; a
                // leg ending at the road's end is all this bend's to use.
                float Avail(int other, float len) => other==0 || other==n-1 ? len : mark[other] ? len-trim[other]-0.25f : len*0.5f;
                float room=0.9f*Mathf.Min(Avail(i-1,Vector2.Distance(p[i-1],p[i])), Avail(i+1,Vector2.Distance(p[i],p[i+1])));
                float tan=(float)Math.Tan(Math.Abs(angle[i])*0.5);
                float t=Mathf.Min(bendRadius*tan, room);
                if(t<0.05f) continue;
                trim[i]=t; rad[i]=t/tan; bendAt[i]=true;
            }
        // Adjacent turns must not consume the same length of road.
        for(int i=0;i<n-1;i++)
            if(!(tightStart[i]>=0 && tightStart[i]==tightStart[i+1]) && trim[i]+trim[i+1]>Vector2.Distance(p[i],p[i+1])-0.25f)
            {
                LastRefusal="switchback: turns too close to fit their curves";
                return false;
            }
        var output=new List<Vector2>(); var flags=new List<bool>(); var curve=new List<bool>(); var widths=new List<float>();
        void Add(Vector2 v,bool flat,bool onArc=false,float w=-1f)
        {
            if(w<0f) w=width;
            if(output.Count>0 && Vector2.Distance(output[output.Count-1],v)<0.001f)
            { flags[flags.Count-1]|=flat; curve[curve.Count-1]|=onArc; widths[widths.Count-1]=Mathf.Min(widths[widths.Count-1],w); return; }
            output.Add(v);flags.Add(flat);curve.Add(onArc);widths.Add(w);
        }
        float step=Mathf.Max(0.25f,width/4);
        void Line(Vector2 end)
        {
            if(output.Count==0) {Add(end,false);return;}
            var from=output[output.Count-1];int steps=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(from,end)/step));
            for(int k=1;k<=steps;k++) Add(from+(end-from)*(k/(float)steps),false);
        }
        void Taper(Vector2 end,float w0,float w1,bool flat)
        {
            var from=output[output.Count-1];int steps=Mathf.Max(1,Mathf.CeilToInt(Vector2.Distance(from,end)/step));
            for(int k=1;k<=steps;k++) Add(from+(end-from)*(k/(float)steps),flat,true,Mathf.Lerp(w0,w1,k/(float)steps));
        }
        bool anyTight=false;
        Add(p[0],false);
        for(int i=1;i<n-1;i++)
        {
            if(tightIn.TryGetValue(i,out var t))
            {
                // In along the leg, converge onto the landing, cross it flat,
                // and spread back out to the leg on the other side.
                float narrow=Mathf.Min(width,TightWidth);
                Line(t.pin);
                Taper(t.l1,width,narrow,false);
                flags[flags.Count-1]=true;
                Taper(t.l2,narrow,narrow,true);
                Taper(t.pout,narrow,width,false);
                anyTight=true;
                i=t.end; continue;
            }
            if(!mark[i] && !bendAt[i]) {Line(p[i]);continue;}
            bool flat=mark[i]; float r=rad[i];
            var incoming=(p[i]-p[i-1]).normalized;var outgoing=(p[i+1]-p[i]).normalized;
            var begin=p[i]-incoming*trim[i];var end=p[i]+outgoing*trim[i];
            float sign=angle[i]<0?-1:1;
            var centre=begin+new Vector2(-incoming.y,incoming.x)*(sign*r);
            Line(begin);flags[flags.Count-1]|=flat;curve[curve.Count-1]=true;
            float start=(float)Math.Atan2(begin.y-centre.y,begin.x-centre.x);
            int count=Mathf.Max(1,Mathf.CeilToInt(r*Mathf.Abs(angle[i])/step));
            for(int k=1;k<count;k++)
            {float a=start+angle[i]*k/count;Add(centre+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*r,flat,true);}
            Add(end,flat,true);
        }
        Line(p[n-1]);
        if (stairs && StairLanding > 0f)
        {
            // Carry each turn's landing StairLanding metres along both legs.
            var along=new float[output.Count];
            for(int k=1;k<output.Count;k++) along[k]=along[k-1]+Vector2.Distance(output[k-1],output[k]);
            var extended=new List<bool>(flags);
            for(int k=0;k<output.Count;k++)
            {
                if(!flags[k]) continue;
                for(int m=k-1;m>=0 && along[k]-along[m]<=StairLanding;m--) extended[m]=true;
                for(int m=k+1;m<output.Count && along[m]-along[k]<=StairLanding;m++) extended[m]=true;
            }
            flags=extended;
        }
        shaped=output;landing=flags;LastCurve=curve;LastWidths=anyTight?widths:null;return true;
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
    /// <summary>
    /// Legs at different elevations must not change each other's road surface.
    ///
    /// By default the two legs' whole footprints (road plus blend margin on
    /// both) may not meet. With <paramref name="surfaceOnly"/> - the staircase
    /// rule - their margins may overlap, but neither leg's reach may touch the
    /// other's surface. The terrain fit weighs every road
    /// point whose influence reaches a vertex, so a vertex on the lower leg's
    /// surface inside the upper leg's reach is pulled toward it: the centres
    /// must be at least half the road plus the other leg's whole reach apart
    /// (road half-width, blend margin and the most the batter may add).
    /// Points of one landing may sit together: that is what a staircase turn is.
    /// </summary>
    public static bool Separated(List<Vector2> points, List<float> heights, float width, IReadOnlyList<bool>? landing = null, bool surfaceOnly = false,
        IReadOnlyList<float>? widths = null)
    {
        float W(int k) => widths != null && k < widths.Count ? widths[k] : width;
        // Points of one landing may sit together: that is what a staircase
        // turn is. Each unbroken run of landing points is one landing.
        int[]? run=null;
        if(landing!=null)
        {
            run=new int[points.Count]; int id=0;
            for(int k=0;k<points.Count;k++)
                run[k]= k<landing.Count && landing[k] ? ((k>0 && k-1<landing.Count && landing[k-1]) ? id : ++id) : 0;
        }
        float reach=width+2*RoadConstants.TerrainBlendMargin;
        float apart=surfaceOnly ? width*0.5f+RoadTerrainModifier.MaxInfluenceRadius(width) : reach;
        // Bins must be at least the largest separation compared, or a close
        // pair in non-neighbouring bins is never compared.
        float binSize=Mathf.Max(reach, surfaceOnly ? Mathf.Max(width,widths==null?width:System.Linq.Enumerable.Max(widths))*0.5f+RoadTerrainModifier.MaxInfluenceRadius(widths==null?width:System.Linq.Enumerable.Max(widths)) : reach);
        float Apart(int i,int j) => widths==null ? apart : surfaceOnly
            ? Mathf.Max(W(i)*0.5f+RoadTerrainModifier.MaxInfluenceRadius(W(j)), W(j)*0.5f+RoadTerrainModifier.MaxInfluenceRadius(W(i)))
            : (W(i)+W(j))*0.5f+2*RoadConstants.TerrainBlendMargin;
        var bins=new Dictionary<Vector2i,List<int>>();
        var distance=new float[points.Count];
        for(int i=0;i<points.Count;i++)
        {
            if(i>0) distance[i]=distance[i-1]+Vector2.Distance(points[i-1],points[i]);
            var cell=new Vector2i(Mathf.FloorToInt(points[i].x/binSize),Mathf.FloorToInt(points[i].y/binSize));
            for(int z=-1;z<=1;z++) for(int x=-1;x<=1;x++)
                if(bins.TryGetValue(new Vector2i(cell.x+x,cell.y+z),out var list))
                    foreach(int j in list)
                    {
                        if(distance[i]-distance[j]<reach*2) continue;
                        if(run!=null && run[i]!=0 && run[i]==run[j]) continue;
                        if(Vector2.Distance(points[i],points[j])<Apart(i,j) && Mathf.Abs(heights[i]-heights[j])>0.5f)
                            return false;
                    }
            if(!bins.TryGetValue(cell,out var bucket)) bins[cell]=bucket=new List<int>();
            bucket.Add(i);
        }
        return true;
    }
}
