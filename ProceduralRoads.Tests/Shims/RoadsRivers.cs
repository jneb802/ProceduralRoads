// Roads' part of the WorldGenerator double (Valheim.Testing.Doubles): the game's river points and the two members of the game's
// WorldGenerator that read them, as the game writes them (Valheim's WorldGenerator.RiverPoint, m_riverPoints, GetRiverGrid,
// GetWeight). The mod reads them through the publicized assembly (RiverWeightReads).
public partial class WorldGenerator
{
    public struct RiverPoint
    {
        public UnityEngine.Vector2 p;
        public float w;
        public float w2;
        public RiverPoint(UnityEngine.Vector2 p_p, float p_w)
        {
            p = p_p;
            w = p_w;
            w2 = (float)((double)p_w * (double)p_w);
        }
    }

    public System.Collections.Generic.Dictionary<Vector2i, RiverPoint[]> m_riverPoints = new();

    public Vector2i GetRiverGrid(float wx, float wy)
    {
        int x = UnityEngine.Mathf.FloorToInt((float)(((double)wx + 32.0) / 64.0));
        int y = UnityEngine.Mathf.FloorToInt((float)(((double)wy + 32.0) / 64.0));
        return new Vector2i(x, y);
    }

    public void GetWeight(RiverPoint[] points, float wx, float wy, out float weight, out float width)
    {
        UnityEngine.Vector2 vector = new UnityEngine.Vector2(wx, wy);
        weight = 0f;
        width = 0f;
        float num = 0f;
        float num2 = 0f;
        for (int i = 0; i < points.Length; i++)
        {
            RiverPoint riverPoint = points[i];
            float num3 = UnityEngine.Vector2.SqrMagnitude(riverPoint.p - vector);
            if (num3 < riverPoint.w2)
            {
                float num4 = (float)System.Math.Sqrt(num3);
                float num5 = (float)(1.0 - (double)num4 / (double)riverPoint.w);
                if (num5 > weight)
                    weight = num5;
                num = (float)((double)num + (double)riverPoint.w * (double)num5);
                num2 = (float)((double)num2 + (double)num5);
            }
        }
        if (num2 > 0f)
            width = (float)((double)num / (double)num2);
    }
}
