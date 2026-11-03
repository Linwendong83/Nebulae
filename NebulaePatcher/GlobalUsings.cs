// The patch tree is organised by domain (UI/Combat/Factory/World/Game); these global usings keep
// the previous single-namespace behaviour so a patch can reference a sibling helper without
// repeating five using directives in every file.
global using NebulaePatcher.Patches.Dynamic.Combat;
global using NebulaePatcher.Patches.Dynamic.Factory;
global using NebulaePatcher.Patches.Dynamic.Game;
global using NebulaePatcher.Patches.Dynamic.UI;
global using NebulaePatcher.Patches.Dynamic.World;
