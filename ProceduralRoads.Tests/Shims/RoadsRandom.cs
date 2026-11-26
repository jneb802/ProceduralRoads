// Roads' part of the Random double (Valheim.Testing.Doubles): Unity's Random.state, the generator's whole state read and put
// back (SeededLocationTurn keeps it round the seeded turn). The double's generator is a System.Random; its state is copied
// whole, so putting a state back makes the same draws after it again, as Unity's does.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine;

public static partial class Random
{
    /// <summary>Unity's saved generator state: a copy, never the generator in use.</summary>
    public struct State
    {
        internal System.Random? Generator;
    }

    public static State state
    {
        get => new State { Generator = (System.Random)Copy(s_random, new Dictionary<object, object>(SameObject.Instance)) };
        set => s_random = (System.Random)Copy(value.Generator ?? new System.Random(0), new Dictionary<object, object>(SameObject.Instance));
    }

    private sealed class SameObject : IEqualityComparer<object>
    {
        public static readonly SameObject Instance = new();
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
    }

    private static readonly MethodInfo s_memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>A deep copy of <paramref name="o"/>: every object and array it reaches copied once.</summary>
    private static object Copy(object o, Dictionary<object, object> copied)
    {
        Type type = o.GetType();
        if (type.IsPrimitive || type.IsEnum || o is string || o is Type) return o;
        if (copied.TryGetValue(o, out object? done)) return done;
        if (o is Array array)
        {
            var a = (Array)array.Clone();
            copied[o] = a;
            if (!type.GetElementType()!.IsPrimitive)
                for (int i = 0; i < a.Length; i++)
                    if (a.GetValue(i) is object item) a.SetValue(Copy(item, copied), i);
            return a;
        }
        object copy = s_memberwiseClone.Invoke(o, null)!;
        copied[o] = copy;
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.IsLiteral) continue;
                if (field.GetValue(copy) is object value) field.SetValue(copy, Copy(value, copied));
            }
        return copy;
    }
}
