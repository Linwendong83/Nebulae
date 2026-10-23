#region

using System;
using System.Collections.Concurrent;
using UnityEngine;

#endregion

namespace NebulaeModel.Utils;

public class UnityDispatchQueue : MonoBehaviour
{
    private static UnityDispatchQueue _instance;

    private static readonly MainThreadActionQueue actionsQueue = new();

    private void Update()
    {
        actionsQueue.Drain();
    }

    private void OnDestroy()
    {
        _instance = null;
    }

    /// <summary>Called from plugin Awake, before sockets start. Unity APIs stay on that thread.</summary>
    public static void Initialize()
    {
        if (_instance) return;
        var go = new GameObject(nameof(UnityDispatchQueue));
        _instance = go.AddComponent<UnityDispatchQueue>();
        DontDestroyOnLoad(_instance);
    }

    public static void RunOnMainThread(Action action)
    {
        // In particular, do not FindObjectOfType/AddComponent from WebSocket callbacks.
        actionsQueue.Enqueue(action);
    }
}

internal sealed class MainThreadActionQueue
{
    private readonly ConcurrentQueue<Action> actions = new();
    public void Enqueue(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        actions.Enqueue(action);
    }
    public void Drain()
    {
        while (actions.TryDequeue(out var action)) action();
    }
}
