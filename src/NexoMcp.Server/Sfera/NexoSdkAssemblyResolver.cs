using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace NexoMcp.Server.Sfera;

/// <summary>
/// Sfera loads a chunk of the rest of the nexo SDK by simple assembly name at runtime
/// (confirmed: InsERT.Moria.Sfera.DanePolaczenia.Jawne pulls in InsERT.Mox.Launcher this way,
/// and there may be others) rather than through assemblies this project references directly.
/// On classic .NET Framework, just having the DLL next to the exe was enough ("private binary
/// probing"); .NET 8 has no such implicit fallback for an <see cref="Assembly.Load(AssemblyName)"/>
/// call; a by-name load that isn't in this app's deps.json simply fails with
/// <see cref="FileNotFoundException"/>, even with the file sitting right next to it. This hooks
/// the default load context so any such lookup also tries the directory this executable (and
/// the nexo SDK assemblies copied next to it - see NexoMcp.Server.csproj's
/// CopyNexoSdkRuntimeFiles target) actually lives in.
/// </summary>
public static class NexoSdkAssemblyResolver
{
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        _registered = true;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (string.IsNullOrEmpty(name.Name))
                return null;

            string candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }
}
