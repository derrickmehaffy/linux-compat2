using System.Diagnostics;
using System.Reflection;
using ClientPlugin.Preloading;
using HarmonyLib;

// Standalone check: installs every LinuxCompat Harmony patch against the original game
// binaries without starting the game. All transpilers execute during installation, so this
// validates every IL anchor. Run with the Game2 directory as the first argument (defaults
// to the standard Steam location) and SE2_NATIVE_DIR pointing at the native libraries.

string game2 =
    args.Length > 0
        ? args[0]
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".steam",
            "debian-installation",
            "steamapps",
            "common",
            "SpaceEngineers2",
            "Game2"
        );
if (!Directory.Exists(game2))
{
    Console.Error.WriteLine($"Game2 directory not found: {game2}");
    return 1;
}

// Pulsar's Steamworks wrapper takes precedence over the game copy, then the game directory,
// mirroring the resolver order in Pulsar's Modern launcher.
string pulsarLibraries =
    Environment.GetEnvironmentVariable("PULSAR_LIBRARIES")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Pulsar",
        "Libraries",
        "Modern"
    );
AppDomain.CurrentDomain.AssemblyResolve += (_, resolveArgs) =>
{
    string name = new AssemblyName(resolveArgs.Name).Name!;
    foreach (string directory in new[] { pulsarLibraries, game2 })
    {
        string path = Path.Combine(directory, name + ".dll");
        if (File.Exists(path))
            return Assembly.LoadFrom(path);
    }
    return null;
};

try
{
    CheckSteamPrepatch(game2);
    Preloader.Initialize();
    Preloader.Finish();
    int patchedMethods = Harmony.GetAllPatchedMethods().Count();
    if (patchedMethods != 80)
        throw new InvalidOperationException(
            $"Expected 80 patched methods, found {patchedMethods}."
        );
    CheckTimestampPatches();
    Console.WriteLine($"OK: {patchedMethods} LinuxCompat methods patched against {game2}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAILED: " + exception);
    return 2;
}

// The floating object spawn time must not overflow at uptimes past the vanilla limit (about
// 10.7 days on Linux), and the continuation queue clock must follow the real one even though
// this machine has been up long enough for the vanilla formula to wrap around.
static void CheckTimestampPatches()
{
    const BindingFlags all =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    Assembly plugin = typeof(Preloader).Assembly;

    MethodInfo spawnTime = plugin
        .GetType("ClientPlugin.Patches.SystemAbstraction.FloatingObjectTrashRemovalPatch", true)!
        .GetMethod("SpawnTime", all)!;
    long thirtyDays = 30L * 86400 * Stopwatch.Frequency;
    var spawned = (TimeSpan)spawnTime.Invoke(null, [thirtyDays, 0L])!;
    if (Math.Abs((spawned - TimeSpan.FromDays(30)).Ticks) > 1)
        throw new InvalidOperationException($"SpawnTime(30 days) returned {spawned}.");
    if ((TimeSpan)spawnTime.Invoke(null, [thirtyDays + Stopwatch.Frequency, 0L])! <= spawned)
        throw new InvalidOperationException("SpawnTime is not increasing.");
    bool vanillaThrows = false;
    try
    {
        TimeSpan.FromMilliseconds(thirtyDays, 0L);
    }
    catch (ArgumentOutOfRangeException)
    {
        vanillaThrows = true;
    }
    Console.WriteLine(
        $"OK: SpawnTime at 30 days of uptime is {spawned} (vanilla throws: {vanillaThrows})"
    );

    Type component = Type.GetType(
        "Keen.Game2.Simulation.GameSystems.TrashRemoval.FloatingObjectTrashRemovalSessionComponent, Game2.Simulation",
        true
    )!;
    MethodInfo added = component
        .GetMethods(all)
        .Single(method => method.Name.Contains("g__OnFloatingObjectAdded|"));
    List<CodeInstruction> patched = PatchProcessor.GetCurrentInstructions(added);
    if (
        !patched.Any(instruction => Equals(instruction.operand, spawnTime))
        || patched.Any(instruction =>
            instruction.operand is MethodInfo { Name: nameof(TimeSpan.FromMilliseconds) }
        )
    )
        throw new InvalidOperationException("OnFloatingObjectAdded does not call SpawnTime.");
    Console.WriteLine("OK: OnFloatingObjectAdded calls SpawnTime");

    Type queueType = Type.GetType(
        "Keen.VRage.Library.Threading.ContinuationQueue, VRage.Library",
        true
    )!;
    var queue = (IDisposable)Activator.CreateInstance(queueType, ["InstallSmoke", null])!;
    using (queue)
    {
        long ticks = (long)queueType.GetProperty("Ticks", all)!.GetValue(queue)!;
        long expected = Stopwatch.GetElapsedTime(0).Ticks;
        long vanilla = Stopwatch.GetTimestamp() * 10000000 / Stopwatch.Frequency;
        if (Math.Abs(ticks - expected) > TimeSpan.TicksPerSecond)
            throw new InvalidOperationException(
                $"ContinuationQueue.Ticks is {ticks}, expected about {expected}."
            );
        Console.WriteLine(
            $"OK: ContinuationQueue.Ticks is {TimeSpan.FromTicks(ticks)} (vanilla formula: {TimeSpan.FromTicks(vanilla)})"
        );
    }
}

// Applies the Cecil rewrite the Pulsar preloader performs, loads the result, and force-JITs
// the two rewritten methods so bad IL or unresolvable Steamworks members fail here instead
// of mid-startup.
static void CheckSteamPrepatch(string game2)
{
    string patched = Path.Combine(
        Path.GetTempPath(),
        $"LinuxCompat-SteamPrepatch-{Environment.ProcessId}"
    );
    Directory.CreateDirectory(patched);
    string output = Path.Combine(patched, "VRage.Steam.dll");

    var resolver = new Mono.Cecil.DefaultAssemblyResolver();
    resolver.AddSearchDirectory(game2);
    using (
        var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(
            Path.Combine(game2, "VRage.Steam.dll"),
            new Mono.Cecil.ReaderParameters { AssemblyResolver = resolver }
        )
    )
    {
        // Always with the opt-in rewrite on: the check exists to validate every IL anchor, and
        // an anchor that is only exercised when an environment variable is set is exactly the
        // one that rots unnoticed.
        SteamPrepatch.Apply(assembly, disableForcedRedownload: true);
        // Pulsar clears the R2R native code when writing preloader-patched assemblies.
        assembly.MainModule.Attributes |= Mono.Cecil.ModuleAttributes.ILOnly;
        assembly.Write(output);
    }

    Assembly steam = Assembly.LoadFrom(output);
    foreach (
        (string typeName, string methodName) in new[]
        {
            ("Keen.VRage.Steam.UGC.SteamUGCServiceComponent", "RefreshSubscribedItemSet"),
            ("Keen.VRage.Steam.EngineComponents.SteamGameServiceComponent", "InitializeAsUser"),
        }
    )
    {
        MethodInfo method =
            steam
                .GetType(typeName, throwOnError: true)!
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeName, methodName);
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(method.MethodHandle);
        Console.WriteLine($"OK: prepared {typeName}.{methodName}");
    }

    // The forced-redownload rewrite lands in a compiler-generated async state machine, which is
    // named rather than declared, so it is looked up the same way the patch does.
    Type component = steam.GetType(
        "Keen.VRage.Steam.UGC.SteamUGCServiceComponent",
        throwOnError: true
    )!;
    Type downloadItem =
        component
            .GetNestedTypes(BindingFlags.NonPublic)
            .SingleOrDefault(nested =>
                nested.Name.StartsWith("<DownloadItem>d__", StringComparison.Ordinal)
            )
        ?? throw new InvalidOperationException("DownloadItem state machine not found.");
    MethodInfo moveNext =
        downloadItem.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(downloadItem.Name, "MoveNext");
    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(moveNext.MethodHandle);
    Console.WriteLine($"OK: prepared {component.FullName}.{downloadItem.Name}.MoveNext");
}
