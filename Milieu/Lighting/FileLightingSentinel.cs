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

    /// <summary>Night while shown's own sentinel (docs/adr/0012), the same mechanism with another file.</summary>
    public const string NightFileName = "night-attempt.flag";

    /// <summary>Sun off's own sentinel (docs/adr/0013), the same mechanism with another file.</summary>
    public const string SunOffFileName = "sun-off-attempt.flag";

    private readonly string path;
    private readonly ILogSink log;
    private readonly string label;

    public bool WasSetAtStartup { get; }

    /// <param name="label">The name in log lines ("Lighting sentinel", "Night sentinel").</param>
    public FileLightingSentinel(string path, ILogSink log, string label = "Lighting sentinel")
    {
        this.path = path;
        this.log = log;
        this.label = label;
        try { WasSetAtStartup = File.Exists(path); }
        catch (Exception e) when (IsIo(e)) { log.Debug($"[Milieu] {label} unreadable: " + LogText.Describe(e, path)); }
    }

    public void Set()
    {
        try { File.WriteAllBytes(path, []); }
        catch (Exception e) when (IsIo(e)) { log.Debug($"[Milieu] {label} not written: " + LogText.Describe(e, path)); }
    }

    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (IsIo(e)) { log.Debug($"[Milieu] {label} not cleared: " + LogText.Describe(e, path)); }
    }

    private static bool IsIo(Exception e) => e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
