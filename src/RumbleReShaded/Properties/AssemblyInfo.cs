using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

[assembly: MelonInfo(typeof(RumbleReShaded.RumbleReShadedMod), RumbleReShaded.BuildInfo.ModName, RumbleReShaded.BuildInfo.ModVersion, RumbleReShaded.BuildInfo.Author)]
[assembly: MelonGame("Buckethead Entertainment", "RUMBLE")]
// UIFramework must initialise before us so UI.RegisterMelon works in OnInitializeMelon.
[assembly: MelonAdditionalDependencies("UIFramework")]

[assembly: AssemblyTitle("RumbleReShaded")]
[assembly: AssemblyDescription("Loads community shader packs and applies them as VR-safe post effects")]
[assembly: AssemblyProduct("RumbleReShaded")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: ComVisible(false)]
[assembly: Guid("9d54c1aa-3e87-4f60-92cb-7b18e5a0d427")]
// Numeric only — .NET assembly versions can't carry a "-dev" prerelease tag.
// The canonical/prerelease version lives in BuildInfo.ModVersion (→ MelonInfo); keep the
// X.Y.Z base here in sync with it (drop the suffix).
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
