using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProceduralRoads;

/// <summary>
/// The loading-screen question for an explored world's first road network (ExistingWorldFirstLoad): four choices,
/// each one an answer the lifecycle carries out. Host only; drawn like RoadGenerationPanel, on the loading
/// indicator's canvas.
/// </summary>
internal static class ExistingWorldChoicePanel
{
    private static GameObject? panel;
    private static Hud? hud;
    private static CanvasGroup? interaction;

    internal static bool OwnsPointer => panel != null && panel.activeInHierarchy && hud == Hud.instance
        && RoadLifecycleManager.ChoicePending && !VanillaModal;
    private static bool VanillaModal => Menu.IsActive() || Menu.IsVisible() || UnifiedPopup.IsVisible()
        || TextInput.IsVisible() || ZNet.IsPasswordDialogShowing();

    /// <summary>The choices, the recommended one (the default where nobody is asked) first and selected.</summary>
    private static readonly (string label, string detail, Action choose)[] Choices =
    {
        ("Build roads through explored land too (recommended)",
            "Roads are built everywhere except the zones with your buildings or terrain edits, which they go round. " +
            "The world is backed up first. Use road_connect afterwards to join your builds to the network.",
            () => RoadLifecycleManager.Answer(ExistingWorldRoads.IncludeExplored)),
        ("Build roads in new land only",
            "Roads are built, but only in land explored from now on. Everything explored so far stays as it is.",
            () => RoadLifecycleManager.Answer(ExistingWorldRoads.NewZonesOnly)),
        ("Skip road building",
            "No road network. The world stays exactly as it is. You can still build one later with road_generate.",
            () => RoadLifecycleManager.Answer(ExistingWorldRoads.Skip)),
        ("Back to the menu",
            "Decide nothing now. You are asked again the next time this world loads.",
            () => RoadLifecycleManager.CancelToMenu()),
    };

    internal static void Clear()
    {
        if (panel != null) UnityEngine.Object.Destroy(panel);
        panel = null; hud = null; interaction = null;
    }

    internal static void Show(Hud owner)
    {
        if (panel == null || hud != owner) Build(owner);
        if (interaction == null) return;
        interaction.alpha = VanillaModal ? 0 : 1;
        interaction.interactable = !VanillaModal;
        interaction.blocksRaycasts = !VanillaModal;
    }

    private static void Build(Hud owner)
    {
        Clear();
        var indicator = owner.m_loadingIndicator;
        if (indicator == null) return;
        var canvas = indicator.GetComponentInParent<Canvas>();
        var template = indicator.GetComponentInChildren<TMP_Text>(true);
        if (canvas == null || template == null) return;
        hud = owner;
        panel = new GameObject("Existing world roads", typeof(RectTransform), typeof(Image), typeof(CanvasGroup), typeof(Canvas), typeof(GraphicRaycaster));
        panel.transform.SetParent(canvas.transform, false);
        interaction = panel.GetComponent<CanvasGroup>();
        interaction.ignoreParentGroups = true;
        panel.GetComponent<Canvas>().overrideSorting = false;
        var rect = (RectTransform)panel.transform;
        rect.anchorMin = new Vector2(.18f, .14f); rect.anchorMax = new Vector2(.82f, .82f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        var backing = panel.GetComponent<Image>(); backing.color = new Color(0, 0, 0, .82f); backing.raycastTarget = true;

        var title = MakeText("Title", panel.transform, template, 24, TextAlignmentOptions.Center);
        Place((RectTransform)title.transform, 1f, 1f, -52, -14);
        title.text = "This world was explored before it had roads";
        var body = MakeText("Explanation", panel.transform, template, 16, TextAlignmentOptions.Center);
        Place((RectTransform)body.transform, 1f, 1f, -104, -56);
        body.textWrappingMode = TextWrappingModes.Normal;
        body.text = $"{NetworkCutoff.ExploredAtLoadCount} zone(s) were explored without Procedural Roads. " +
                    "Choose how the road network treats them. This is asked once, on this world's first load with the mod.";

        const float buttonHeight = 64f, gap = 10f, top = 116f;
        for (int i = 0; i < Choices.Length; i++)
        {
            var (label, detail, choose) = Choices[i];
            var buttonObject = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(panel.transform, false);
            var buttonRect = (RectTransform)buttonObject.transform;
            buttonRect.anchorMin = new Vector2(.05f, 1); buttonRect.anchorMax = new Vector2(.95f, 1);
            buttonRect.pivot = new Vector2(.5f, 1);
            buttonRect.anchoredPosition = new Vector2(0, -(top + i * (buttonHeight + gap)));
            buttonRect.sizeDelta = new Vector2(0, buttonHeight);
            var image = buttonObject.GetComponent<Image>(); image.color = Color.white;
            var button = buttonObject.GetComponent<Button>(); button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            var colors = button.colors;
            colors.normalColor = new Color(.25f, .25f, .25f, .95f); colors.highlightedColor = new Color(.50f, .45f, .22f, 1);
            colors.pressedColor = new Color(.68f, .56f, .20f, 1); colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(.12f, .12f, .12f, .65f); colors.fadeDuration = .1f; button.colors = colors;
            // One answer per panel: the lifecycle ignores a second, and the panel goes when the question does.
            button.onClick.AddListener(() => choose());
            // The recommended answer starts selected: highlighted, and what a keyboard or gamepad confirms.
            if (i == 0) button.Select();

            var heading = MakeText("Label", buttonRect, template, 19, TextAlignmentOptions.TopLeft);
            var headingRect = (RectTransform)heading.transform;
            headingRect.anchorMin = new Vector2(0, 1); headingRect.anchorMax = Vector2.one; headingRect.pivot = new Vector2(.5f, 1);
            headingRect.offsetMin = new Vector2(12, -28); headingRect.offsetMax = new Vector2(-12, -6);
            heading.text = label;
            var explanation = MakeText("Detail", buttonRect, template, 14, TextAlignmentOptions.TopLeft);
            var explanationRect = (RectTransform)explanation.transform;
            explanationRect.anchorMin = Vector2.zero; explanationRect.anchorMax = Vector2.one;
            explanationRect.offsetMin = new Vector2(12, 4); explanationRect.offsetMax = new Vector2(-12, -28);
            explanation.textWrappingMode = TextWrappingModes.Normal;
            explanation.color = new Color(.85f, .85f, .85f, 1);
            explanation.text = detail;
        }
    }

    /// <summary>A strip across the panel, from <paramref name="bottom"/> to <paramref name="top"/> pixels below its top edge.</summary>
    private static void Place(RectTransform rect, float anchorMinY, float anchorMaxY, float bottom, float top)
    {
        rect.anchorMin = new Vector2(0, anchorMinY); rect.anchorMax = new Vector2(1, anchorMaxY);
        rect.pivot = new Vector2(.5f, 1);
        rect.offsetMin = new Vector2(16, bottom); rect.offsetMax = new Vector2(-16, top);
    }

    private static TMP_Text MakeText(string name, Transform parent, TMP_Text template, float size, TextAlignmentOptions alignment)
    {
        var item = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); item.transform.SetParent(parent, false);
        var text = item.GetComponent<TextMeshProUGUI>(); text.font = template.font; text.fontSharedMaterial = template.fontSharedMaterial;
        text.fontSize = size; text.alignment = alignment; text.raycastTarget = false; return text;
    }
}
