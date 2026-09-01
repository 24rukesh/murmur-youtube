using System.Runtime.InteropServices;

namespace Murmur.App;

/// <summary>
/// Prints every key event the low-level hook sees, for fifteen seconds.
/// </summary>
/// <remarks>
/// <para>
/// This exists for one question: <b>what does the Copilot key on this machine actually
/// send?</b> The common answer is a three-event chord — Left Win down, Left Shift down, F23
/// down, released in reverse — but it is a firmware decision, and some vendors send Win+C
/// instead. Nothing in the app can tell which without watching a real keypress.
/// </para>
/// <para>
/// The expected trace for a press-and-release of the Copilot key is:
/// </para>
/// <code>
/// down  vk=0x5B   (Left Win)
/// down  vk=0xA0   (Left Shift)
/// down  vk=0x86   (F23)      ← the one Murmur binds
/// up    vk=0x86
/// up    vk=0xA0
/// up    vk=0x5B
/// </code>
/// </remarks>
internal static class KeyLog
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    /// <summary>Runs the key logger.</summary>
    /// <returns>0 if the hook installed and saw at least one key.</returns>
    public static int Run()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("--keylog needs Windows.");
            return 1;
        }

        // Deliberately not the Copilot key: binding it here would swallow F23 and hide the
        // very event this is meant to show. Right Ctrl leaves everything passing through.
        var hotkey = PlatformFactory.CreateHotkeySource(0xA3);
        if (hotkey is null)
        {
            Console.WriteLine("Could not load the Windows platform layer.");
            return 1;
        }

        var seen = 0;

        var attached = PlatformFactory.TryAttachTrace(hotkey, line =>
        {
            Interlocked.Increment(ref seen);
            Console.WriteLine("  " + line + Annotate(line));
        });

        if (!attached)
        {
            Console.WriteLine("This build's hook has no trace hook-point.");
            return 1;
        }

        if (!hotkey.Start())
        {
            Console.WriteLine("Could not install the keyboard hook.");
            return 1;
        }

        Console.WriteLine($"Press keys - the Copilot key especially. Listening for {Window.TotalSeconds:0} seconds.");
        Console.WriteLine();

        Thread.Sleep(Window);
        hotkey.Dispose();

        Console.WriteLine();
        Console.WriteLine(seen == 0
            ? "No key events seen. The hook installed but nothing arrived."
            : $"{seen} key events. If 0x86 appeared, bind COPILOT in Settings.");

        return seen == 0 ? 1 : 0;
    }

    /// <summary>Names the handful of codes that matter here, so the trace reads itself.</summary>
    private static string Annotate(string line) => line switch
    {
        _ when line.Contains("vk=0x86", StringComparison.Ordinal) => "   <- F23, the Copilot key's own event",
        _ when line.Contains("vk=0x5B", StringComparison.Ordinal) => "   <- Left Win",
        _ when line.Contains("vk=0xA0", StringComparison.Ordinal) => "   <- Left Shift",
        _ when line.Contains("vk=0x43", StringComparison.Ordinal) => "   <- C (a Win+C style Copilot key)",
        _ => string.Empty,
    };
}
