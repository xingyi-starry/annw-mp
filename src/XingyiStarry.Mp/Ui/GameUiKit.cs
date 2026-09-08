using System;
using ANNW;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace XingyiStarry.Mp.Ui;

internal static class GameUiKit
{
    private static TMP_FontAsset? font;
    private static Sprite? panelSprite;
    private static Color panelColor = new Color(0.18f, 0.1f, 0.055f, 0.98f);
    private static Button? buttonTemplate;
    private static TMP_InputField? inputTemplate;

    internal static bool Ensure()
    {
        if (font != null && buttonTemplate != null) return true;
        var floater = UI_Floater.self;
        if (floater == null) return false;
        if (floater.pop_general != null)
        {
            font = floater.pop_general.txt_title?.font;
            var panel = floater.pop_general.panel;
            var image = panel != null ? panel.GetComponent<Image>() ?? panel.GetComponentInChildren<Image>(true) : null;
            if (image != null) { panelSprite = image.sprite; panelColor = image.color; }
        }
        foreach (var candidate in floater.GetComponentsInChildren<Button>(true))
        {
            if (candidate == null || candidate.GetComponent<Image>()?.sprite == null || candidate.GetComponentInChildren<TextMeshProUGUI>(true) == null) continue;
            buttonTemplate = candidate; break;
        }
        foreach (var candidate in Resources.FindObjectsOfTypeAll<TMP_InputField>())
        {
            if (candidate == null || candidate.gameObject.name.StartsWith("XingyiStarryMp_", StringComparison.Ordinal)) continue;
            inputTemplate = candidate; break;
        }
        if (font == null) font = floater.GetComponentInChildren<TextMeshProUGUI>(true)?.font;
        return font != null;
    }

    internal static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = parent.gameObject.layer;
        var rect = go.GetComponent<RectTransform>(); rect.SetParent(parent, false); rect.localScale = Vector3.one; return rect;
    }

    internal static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
    }

    internal static Image Panel(RectTransform rect, Color? color = null)
    {
        var image = rect.gameObject.AddComponent<Image>(); image.sprite = panelSprite; image.type = panelSprite != null && panelSprite.border.sqrMagnitude > 0.01f ? Image.Type.Sliced : Image.Type.Simple;
        image.color = color ?? panelColor; return image;
    }

    internal static TextMeshProUGUI Text(Transform parent, string name, string value, float size, TextAlignmentOptions alignment)
    {
        var rect = Rect(name, parent); Stretch(rect);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>(); text.font = font; text.text = value; text.fontSize = size; text.alignment = alignment;
        text.color = new Color(0.96f, 0.84f, 0.62f, 1f); text.raycastTarget = false; text.overflowMode = TextOverflowModes.Ellipsis;
        return text;
    }

    internal static Button Button(Transform parent, string name, string label, UnityAction action, float width = 0f)
    {
        GameObject go;
        if (buttonTemplate != null)
        {
            go = UnityEngine.Object.Instantiate(buttonTemplate.gameObject, parent, false);
            go.name = name; go.SetActive(true);
            foreach (var localized in go.GetComponentsInChildren<Localized_Txt>(true)) { localized.enabled = false; UnityEngine.Object.Destroy(localized); }
        }
        else
        {
            go = Rect(name, parent).gameObject; Panel(go.GetComponent<RectTransform>(), new Color(0.55f, 0.25f, 0.06f, 1f)); go.AddComponent<Button>();
        }
        var button = go.GetComponent<Button>() ?? go.GetComponentInChildren<Button>(true);
        button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(action);
        var text = go.GetComponentInChildren<TextMeshProUGUI>(true) ?? Text(go.transform, "Label", label, 20f, TextAlignmentOptions.Center);
        text.text = label; text.font = font; text.fontSize = 20f;
        text.enableAutoSizing = true; text.fontSizeMin = 10f; text.fontSizeMax = 20f;
        var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        layout.ignoreLayout = false; layout.minHeight = 42f; layout.preferredHeight = 42f; layout.flexibleHeight = 0f;
        if (width > 0f) { layout.minWidth = width; layout.preferredWidth = width; layout.flexibleWidth = 0f; }
        else { layout.minWidth = 0f; layout.preferredWidth = 0f; layout.flexibleWidth = 1f; }
        return button;
    }

    internal static TMP_InputField Input(Transform parent, string name, string value)
    {
        TMP_InputField input;
        if (inputTemplate != null)
        {
            var go = UnityEngine.Object.Instantiate(inputTemplate.gameObject, parent, false); go.name = name; go.SetActive(true);
            input = go.GetComponent<TMP_InputField>(); input.onValueChanged.RemoveAllListeners(); input.onEndEdit.RemoveAllListeners();
        }
        else
        {
            var rect = Rect(name, parent); Panel(rect, new Color(0.08f, 0.045f, 0.025f, 1f));
            var viewport = Rect("Text Area", rect); Stretch(viewport); viewport.offsetMin = new Vector2(10, 3); viewport.offsetMax = new Vector2(-10, -3);
            viewport.gameObject.AddComponent<RectMask2D>();
            var text = Text(viewport, "Text", value, 20f, TextAlignmentOptions.MidlineLeft);
            input = rect.gameObject.AddComponent<TMP_InputField>(); input.textViewport = viewport; input.textComponent = text;
        }
        input.text = value; input.lineType = TMP_InputField.LineType.SingleLine;
        var layout = input.GetComponent<LayoutElement>() ?? input.gameObject.AddComponent<LayoutElement>(); layout.ignoreLayout = false; layout.minHeight = 44f; layout.preferredHeight = 44f; layout.flexibleWidth = 1f;
        return input;
    }

    internal static void SetLabel(Button? button, string value)
    {
        var text = button?.GetComponentInChildren<TextMeshProUGUI>(true); if (text != null) text.text = value;
    }
}
