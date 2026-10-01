#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// .NET Framework has no ModuleInitializerAttribute; the C# compiler only
    /// needs a type by this name to emit a module initializer, so the test
    /// assembly declares its own for net48.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
