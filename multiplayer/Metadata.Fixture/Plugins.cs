using BepInEx;
using System.Reflection;
[assembly:AssemblyCompany("Metadata fixture company")]
[assembly:AssemblyVersion("3.2.1.0")]

// Compiled against the actual bundled loader. This fixture is parsed as bytes;
// it is never installed, loaded through reflection, or executed by the launcher.
[BepInPlugin("fixture.main", "海上工坊", "2.1.0")]
[BepInDependency("fixture.required", "1.2.0")]
[BepInDependency("fixture.optional", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("fixture.hard", BepInDependency.DependencyFlags.HardDependency)]
[BepInIncompatibility("fixture.conflict")]
[BepInProcess("Flotsam.exe")]
public sealed class MainPlugin : BaseUnityPlugin { }

[BepInPlugin("fixture.secondary", "Fixture second plugin", "1.0.0")]
public sealed class SecondaryPlugin : BaseUnityPlugin { }
