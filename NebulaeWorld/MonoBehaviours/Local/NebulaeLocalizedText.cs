using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NebulaeWorld.MonoBehaviours.Local;

/// <summary>Retains the original key when a mod-owned label is reused or the language changes.</summary>
public sealed class NebulaeLocalizedText : MonoBehaviour
{
    [SerializeField] private string key;

    public static void Set(Component text, string stringKey)
    {
        var localizer = text.GetComponent<Localizer>();
        if (localizer != null) localizer.enabled = false;
        var binding = text.GetComponent<NebulaeLocalizedText>() ?? text.gameObject.AddComponent<NebulaeLocalizedText>();
        binding.key = stringKey;
        binding.Refresh();
    }

    private void OnEnable()
    {
        Localization.OnLanguageChange += Refresh;
        Refresh();
    }

    private void OnDisable()
    {
        Localization.OnLanguageChange -= Refresh;
    }

    private void Refresh()
    {
        if (key == null) return;
        var text = GetComponent<Text>();
        if (text != null) text.text = key.Translate();
        var tmp = GetComponent<TMP_Text>();
        if (tmp != null) tmp.text = key.Translate();
    }
}
