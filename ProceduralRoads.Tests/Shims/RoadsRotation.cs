// Roads' part of the Vector3 and Quaternion doubles (Valheim.Testing.Doubles): the directions, the cross product and
// LookRotation that PlaceVegetation's ground tilt is built from (RoadClearAreaManager.GroundLaid), as Unity defines them.
namespace UnityEngine;

public partial struct Vector3
{
    public static Vector3 up => new(0f, 1f, 0f);
    public static Vector3 forward => new(0f, 0f, 1f);

    /// <summary>Unity's cross product (left-handed: Cross(up, forward) is right).</summary>
    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
}

public partial struct Quaternion
{
    /// <summary>Unity's LookRotation: local z along <paramref name="forward"/>, local y as near <paramref name="upwards"/> as
    /// it can be while square to it (right = upwards x forward, normalised; up = forward x right).</summary>
    public static Quaternion LookRotation(Vector3 forward, Vector3 upwards)
    {
        double fx = forward.x, fy = forward.y, fz = forward.z;
        double fl = System.Math.Sqrt(fx * fx + fy * fy + fz * fz);
        fx /= fl; fy /= fl; fz /= fl;
        double rx = upwards.y * fz - upwards.z * fy, ry = upwards.z * fx - upwards.x * fz, rz = upwards.x * fy - upwards.y * fx;
        double rl = System.Math.Sqrt(rx * rx + ry * ry + rz * rz);
        rx /= rl; ry /= rl; rz /= rl;
        double ux = fy * rz - fz * ry, uy = fz * rx - fx * rz, uz = fx * ry - fy * rx;
        // The rotation whose columns are right, up and forward.
        double m00 = rx, m01 = ux, m02 = fx, m10 = ry, m11 = uy, m12 = fy, m20 = rz, m21 = uz, m22 = fz;
        double trace = m00 + m11 + m22, qx, qy, qz, qw;
        if (trace > 0.0)
        {
            double s = System.Math.Sqrt(trace + 1.0) * 2.0;
            qw = 0.25 * s; qx = (m21 - m12) / s; qy = (m02 - m20) / s; qz = (m10 - m01) / s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
            qw = (m21 - m12) / s; qx = 0.25 * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s;
        }
        else if (m11 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
            qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25 * s; qz = (m12 + m21) / s;
        }
        else
        {
            double s = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
            qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25 * s;
        }
        return new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
    }

    public static float Dot(Quaternion a, Quaternion b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
}
