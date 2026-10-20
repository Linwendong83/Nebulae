using UnityEngine;

namespace NebulaeWorld.MonoBehaviours.Local.Chat;

internal static class ChatInputState
{
    private static int frame = -1;
    private static bool wasComposing;
    private static bool composingThisFrame;

    public static bool IsComposing
    {
        get
        {
            if (frame != Time.frameCount)
            {
                var composing = !string.IsNullOrEmpty(Input.compositionString);
                // Unity may clear compositionString on the same frame Enter commits a candidate.
                composingThisFrame = composing || wasComposing;
                wasComposing = composing;
                frame = Time.frameCount;
            }
            return composingThisFrame;
        }
    }
}
