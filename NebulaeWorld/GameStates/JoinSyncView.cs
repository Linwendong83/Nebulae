using System;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NebulaeWorld.GameStates;

/// <summary>Show the loaded world while replay catches up, keeping the game UI read-only.</summary>
internal sealed class JoinSyncView : IDisposable
{
    private GameObject root;
    private UIGameLoadingSplash promptOwner;
    private string originalPrompt, displayedPrompt;

    public void Update(bool loaded, string message)
    {
        var splash = UIRoot.instance?.gameLoadingSplash;
        if (splash == null) return;
        if (!loaded)
        {
            if (!splash.gameObject.activeSelf) splash.gameObject.SetActive(true);
            SetPrompt(splash, message);
            return;
        }

        RestorePrompt();
        if (root == null) Create(splash);
        if (splash.gameObject.activeSelf) UIRoot.instance.CloseLoadingUI();
    }

    public void SetPrompt(UIGameLoadingSplash splash, string message)
    {
        if (splash == null || splash.promptText == null) return;
        if (promptOwner != splash)
        {
            RestorePrompt();
            promptOwner = splash;
            originalPrompt = splash.promptText.text;
        }
        displayedPrompt = message;
        if (splash.promptText.text != message) splash.promptText.text = message;
    }

    private void RestorePrompt()
    {
        if (promptOwner != null && promptOwner.promptText != null && promptOwner.promptText.text == displayedPrompt)
            promptOwner.promptText.text = originalPrompt;
        promptOwner = null;
        originalPrompt = displayedPrompt = null;
    }

    private void Create(UIGameLoadingSplash splash)
    {
        // A transparent raycast target also blocks direct HUD clicks, which do not all
        // consult VFInput. The existing join input gate still blocks gameplay hotkeys.
        root = new GameObject("Nebulae Join Sync", typeof(RectTransform), typeof(Image));
        var rect = (RectTransform)root.transform;
        rect.SetParent(splash.transform.parent, false);
        rect.SetSiblingIndex(splash.transform.GetSiblingIndex());
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        root.GetComponent<Image>().color = Color.clear;
    }

    public void Dispose()
    {
        RestorePrompt();
        if (root != null)
        {
            // Destroy is deferred until the frame ends; release the click blocker now.
            root.SetActive(false);
            Object.Destroy(root);
        }
        root = null;
    }
}
