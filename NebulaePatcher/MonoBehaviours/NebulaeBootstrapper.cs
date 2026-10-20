#region

using NebulaeModel.Networking;
using NebulaePatcher.Patches.Misc;
using NebulaeWorld;
using NebulaeWorld.Player;
using UnityEngine;

#endregion

namespace NebulaePatcher.MonoBehaviours;

public class NebulaeBootstrapper : MonoBehaviour
{
    private void OnApplicationQuit()
    {
        if (Multiplayer.IsDedicated) HeadlessShieldBackend.Shutdown();
    }

    private void OnDestroy()
    {
        if (Multiplayer.IsDedicated) HeadlessShieldBackend.Shutdown();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);

        // This makes sure that even if the game is minimized, it will still receive and send packets
        Application.runInBackground = true;
    }

    private void LateUpdate()
    {
        if (Multiplayer.IsActive)
        {
            PlayerLatencyTracker.Update();
            (Multiplayer.Session.Network as IServer)?.Update();
            (Multiplayer.Session.Network as IClient)?.Update();
        }
    }
}
