using System;
using System.IO;

namespace Milieu.Lighting;

/// <summary>
/// The crash sentinel as a flag file (<c>&lt;config&gt;/lighting-attempt.flag</c>; its existence is all that counts).
/// <see cref="WasSetAtStartup"/> is read once, here. IO errors are swallowed (logged at Debug): the sentinel never blocks
/// lighting. Scope (docs/adr/0009): creation and the first <see cref="LightRigLayout.StableTicks"/> ticks only; every game
/// client on the machine shares the config directory, so another client's attempt can show a false CrashGuard.
/// </summary>
public sealed class FileLightingSentinel : ILightingSentinel
{
    public const string FileName = "lighting-attempt.flag";

    private readonly string path;
    private readonly ILogSink log;

    public bool WasSetAtStartup { get; }

    public FileLightingSentinel(string path, ILogSink log)
    {
        this.path = path;
        this.log = log;
        try { WasSetAtStartup = File.Exists(path); }
        catch (Exception e) when (IsIo(e)) { log.Debug("[Milieu] Lighting sentinel unreadable: " + LogText.Describe(e, path)); }
    }

    public void Set()
    {
        try { File.WriteAllBytes(path, []); }
        catch (Exception e) when (IsIo(e)) { log.Debug("[Milieu] Lighting sentinel not written: " + LogText.Describe(e, path)); }
    }

    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (IsIo(e)) { log.Debug("[Milieu] Lighting sentinel not cleared: " + LogText.Describe(e, path)); }
    }

    private static bool IsIo(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
