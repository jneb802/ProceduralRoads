using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace ProceduralRoads;

/// <summary>One host-only loading panel, projected exclusively from the owner's detached snapshots.</summary>
internal static partial class RoadGenerationPanel
{
    private static GameObject? panel;
    private static Hud? hud;
    private static readonly List<TMP_Text> rows = new();
    private static readonly List<RectTransform> barFills = new();
    private static RoadGenerationProgress? shown;
    private static Button? cancel;
    private static TMP_Text? cancelText, summary, status;
    private static GameObject? tooltip;
    private static bool hovering;
    private static int visibleCount=-1, laidOutCount=-1;
    private static CanvasGroup? interaction;
    private static RectTransform? viewport, content;
    private static Vector2 layoutSize = new(-1,-1);
    internal static bool OwnsPointer => panel!=null && panel.activeInHierarchy && hud==Hud.instance
        && RoadLifecycleManager.InitialGenerationActive && !VanillaModal;
    private static bool VanillaModal => Menu.IsActive() || Menu.IsVisible() || UnifiedPopup.IsVisible()
        || TextInput.IsVisible() || ZNet.IsPasswordDialogShowing();
    internal static void Synchronize()
    {
        if(!RoadLifecycleManager.InitialGenerationActive || hud==null || hud!=Hud.instance) { Clear(); return; }
        Relayout();
        if(interaction!=null) { interaction.alpha=VanillaModal?0:1; interaction.interactable=!VanillaModal; interaction.blocksRaycasts=!VanillaModal; }
        RefreshTooltip();
    }
    internal static void Clear()
    {
        if(panel!=null) UnityEngine.Object.Destroy(panel);
        panel=null; hud=null; shown=null; cancel=null; cancelText=null; summary=null; status=null; tooltip=null; hovering=false; visibleCount=laidOutCount=-1; interaction=null; viewport=null; content=null; layoutSize=new Vector2(-1,-1); rows.Clear(); barFills.Clear();
    }
    internal static void Show(Hud owner, RoadGenerationProgress progress)
    {
        if(panel==null || hud!=owner || shown?.RunId!=progress.RunId || rows.Count!=Math.Max(1,progress.Islands.Count))
        {
            Clear();
            var indicator=owner.m_loadingIndicator;
            var canvas=indicator.GetComponentInParent<Canvas>();
            var template=indicator.GetComponentInChildren<TMP_Text>(true);
            if(canvas==null || template==null) return;
            hud=owner;
            panel=new GameObject("Road generation",typeof(RectTransform),typeof(Image),typeof(CanvasGroup),typeof(Canvas),typeof(GraphicRaycaster));
            panel.transform.SetParent(canvas.transform,false);
            interaction=panel.GetComponent<CanvasGroup>();
            interaction.ignoreParentGroups=true;
            panel.GetComponent<Canvas>().overrideSorting=false;
            var rect=(RectTransform)panel.transform;
            rect.anchorMin=new Vector2(.10f,.18f); rect.anchorMax=new Vector2(.90f,.78f);
            rect.offsetMin=rect.offsetMax=Vector2.zero;
            var backing=panel.GetComponent<Image>(); backing.color=new Color(0,0,0,.72f); backing.raycastTarget=false;
            var viewObject=new GameObject("Activity viewport",typeof(RectTransform),typeof(Image),typeof(RectMask2D));
            viewObject.transform.SetParent(panel.transform,false);
            var view=(RectTransform)viewObject.transform;
            viewport=view;
            view.anchorMin=Vector2.zero; view.anchorMax=Vector2.one; view.offsetMin=new Vector2(16,56); view.offsetMax=new Vector2(-16,-46);
            viewObject.GetComponent<Image>().color=Color.clear;
            var buttonObject=new GameObject("Cancel and return to menu",typeof(RectTransform),typeof(Image),typeof(Button));
            buttonObject.transform.SetParent(panel.transform,false);
            var buttonRect=(RectTransform)buttonObject.transform;
            buttonRect.anchorMin=new Vector2(.25f,0); buttonRect.anchorMax=new Vector2(.75f,0);
            buttonRect.pivot=new Vector2(.5f,0); buttonRect.anchoredPosition=new Vector2(0,10); buttonRect.sizeDelta=new Vector2(0,36);
            var buttonImage=buttonObject.GetComponent<Image>(); buttonImage.color=Color.white;
            cancel=buttonObject.GetComponent<Button>(); cancel.targetGraphic=buttonImage;
            cancel.transition=Selectable.Transition.ColorTint;
            var colors=cancel.colors;
            colors.normalColor=new Color(.25f,.25f,.25f,.95f); colors.highlightedColor=new Color(.50f,.45f,.22f,1);
            colors.pressedColor=new Color(.68f,.56f,.20f,1); colors.selectedColor=colors.highlightedColor;
            colors.disabledColor=new Color(.12f,.12f,.12f,.65f); colors.fadeDuration=.1f; cancel.colors=colors;
            var boundButton=cancel; long boundRun=progress.RunId;
            buttonObject.AddComponent<RoadGenerationHover>().Changed=value=> {
                if(!ReferenceEquals(cancel,boundButton) || RoadLifecycleManager.Progress.RunId!=boundRun) return;
                hovering=value; RefreshTooltip();
            };
            long expectedRun=progress.RunId;
            cancel.onClick.AddListener(()=> {
                if(RoadLifecycleManager.Progress.Phase=="ReturningToMenu" || RoadLifecycleManager.Progress.Phase=="MenuFailed")
                    RoadLifecycleManager.RetryReturnToMenu(expectedRun);
                else RoadLifecycleManager.RequestCancelToMenu(expectedRun);
            });
            var label=new GameObject("Label",typeof(RectTransform),typeof(TextMeshProUGUI)); label.transform.SetParent(buttonRect,false);
            var labelRect=(RectTransform)label.transform; labelRect.anchorMin=Vector2.zero; labelRect.anchorMax=Vector2.one; labelRect.offsetMin=labelRect.offsetMax=Vector2.zero;
            cancelText=label.GetComponent<TextMeshProUGUI>(); cancelText.font=template.font; cancelText.fontSharedMaterial=template.fontSharedMaterial;
            cancelText.fontSize=18; cancelText.alignment=TextAlignmentOptions.Center; cancelText.raycastTarget=false;
            summary=MakeText("Run summary",panel.transform,template,18,TextAlignmentOptions.Center);
            var summaryRect=(RectTransform)summary.transform; summaryRect.anchorMin=new Vector2(0,1); summaryRect.anchorMax=Vector2.one;
            summaryRect.pivot=new Vector2(.5f,1); summaryRect.offsetMin=new Vector2(12,-40); summaryRect.offsetMax=new Vector2(-12,-8);
            status=MakeText("Generation status",view,template,18,TextAlignmentOptions.Center);
            var statusRect=(RectTransform)status.transform; statusRect.anchorMin=Vector2.zero; statusRect.anchorMax=Vector2.one;
            statusRect.offsetMin=statusRect.offsetMax=Vector2.zero;
            tooltip=new GameObject("Cancel explanation",typeof(RectTransform),typeof(Image)); tooltip.transform.SetParent(panel.transform,false);
            tooltip.GetComponent<Image>().color=new Color(0,0,0,.96f); tooltip.GetComponent<Image>().raycastTarget=false;
            var tipRect=(RectTransform)tooltip.transform; tipRect.anchorMin=new Vector2(.03f,0); tipRect.anchorMax=new Vector2(.97f,0);
            tipRect.pivot=new Vector2(.5f,0); tipRect.anchoredPosition=new Vector2(0,50); tipRect.sizeDelta=new Vector2(0,58);
            var tip=MakeText("Explanation",tipRect,template,15,TextAlignmentOptions.Center);
            var tipTextRect=(RectTransform)tip.transform; tipTextRect.anchorMin=Vector2.zero; tipTextRect.anchorMax=Vector2.one;
            tipTextRect.offsetMin=new Vector2(8,4); tipTextRect.offsetMax=new Vector2(-8,-4); tip.textWrappingMode=TextWrappingModes.Normal;
            tip.text="Discard this unfinished road network and return to the menu. The world and character may still be saved.";
            tooltip.SetActive(false);
            var contentObject=new GameObject("Island slots",typeof(RectTransform)); contentObject.transform.SetParent(view,false);
            var area=(RectTransform)contentObject.transform;
            content=area;
            area.anchorMin=new Vector2(0,1); area.anchorMax=Vector2.one; area.pivot=new Vector2(.5f,1);
            area.offsetMin=area.offsetMax=Vector2.zero;
            var scroll=panel.AddComponent<ScrollRect>(); scroll.viewport=view; scroll.content=area;
            scroll.horizontal=false; scroll.vertical=true; scroll.movementType=ScrollRect.MovementType.Clamped; scroll.scrollSensitivity=28;
            int count=Math.Max(1,progress.Islands.Count);
            for(int i=0;i<count;i++)
            {
                var item=new GameObject("Island slot "+(i+1),typeof(RectTransform),typeof(TextMeshProUGUI)); item.transform.SetParent(area,false);
                var text=item.GetComponent<TextMeshProUGUI>(); text.font=template.font; text.fontSharedMaterial=template.fontSharedMaterial;
                text.textWrappingMode=TextWrappingModes.NoWrap; text.raycastTarget=false; text.alignment=TextAlignmentOptions.TopLeft;
                rows.Add(text);
                // A thin bar along the bottom of the row: a dark track and a fill whose width is the settled fraction.
                var track=new GameObject("Progress track",typeof(RectTransform),typeof(Image)); track.transform.SetParent(item.transform,false);
                var trackRect=(RectTransform)track.transform; trackRect.anchorMin=Vector2.zero; trackRect.anchorMax=new Vector2(1,0);
                trackRect.pivot=new Vector2(.5f,0); trackRect.offsetMin=new Vector2(0,2); trackRect.offsetMax=new Vector2(0,6);
                var trackImage=track.GetComponent<Image>(); trackImage.color=new Color(1,1,1,.18f); trackImage.raycastTarget=false;
                var fillObject=new GameObject("Progress fill",typeof(RectTransform),typeof(Image)); fillObject.transform.SetParent(track.transform,false);
                var fill=(RectTransform)fillObject.transform; fill.anchorMin=Vector2.zero; fill.anchorMax=new Vector2(0,1);
                fill.offsetMin=fill.offsetMax=Vector2.zero;
                var fillImage=fillObject.GetComponent<Image>(); fillImage.color=new Color(.85f,.72f,.30f,.95f); fillImage.raycastTarget=false;
                barFills.Add(fill);
            }
        }
        // The owner coalesces activity snapshots; avoid text/mesh allocation on every HUD frame.
        if(ReferenceEquals(shown,progress)) { Synchronize(); return; }
        shown=progress;
        visibleCount=progress.ActiveIslands.Count;
        if(cancel!=null) cancel.interactable=progress.Phase=="Preparing" || progress.Phase=="Generating" || progress.Phase=="ReturningToMenu" || progress.Phase=="MenuFailed";
        if(cancelText!=null) cancelText.text=progress.Phase=="Stopping" ? "Stopping generation…" : progress.Phase=="ReturningToMenu" || progress.Phase=="MenuFailed" ? "Return to menu" : "Cancel and return to menu";
        bool stopped=progress.Phase=="ReturningToMenu" || progress.Phase=="MenuFailed";
        string length=progress.TotalKilometres.HasValue ? $"{progress.TotalKilometres:F1} km committed" : "committed length unknown (legacy save)";
        if(summary!=null) summary.text=progress.Phase=="Preparing" ? $"Preparing roads · {(int)progress.ElapsedSeconds/60}m {(int)progress.ElapsedSeconds%60:00}s elapsed" : stopped ? "Road generation stopped; unpublished roads discarded." :
            $"{length} · {progress.Completed}/{progress.Total} islands complete · {visibleCount} island{(visibleCount==1?"":"s")} active · {(int)progress.ElapsedSeconds/60}m {(int)progress.ElapsedSeconds%60:00}s elapsed";
        if(status!=null) { status.gameObject.SetActive(visibleCount==0); status.text=progress.Phase=="Generating" ? "Waiting for island work…" : progress.Text; }
        for(int i=0;i<rows.Count;i++)
        {
            rows[i].gameObject.SetActive(i<visibleCount);
            if(i>=visibleCount) continue;
            var row=progress.ActiveIslands[i];
            rows[i].text=RowText(row);
            barFills[i].anchorMax=new Vector2(Fraction(row),1);
        }
        if(tooltip!=null) tooltip.transform.SetAsLastSibling();
        Synchronize();
    }
    private static TMP_Text MakeText(string name,Transform parent,TMP_Text template,float size,TextAlignmentOptions alignment)
    {
        var item=new GameObject(name,typeof(RectTransform),typeof(TextMeshProUGUI)); item.transform.SetParent(parent,false);
        var text=item.GetComponent<TextMeshProUGUI>(); text.font=template.font; text.fontSharedMaterial=template.fontSharedMaterial;
        text.fontSize=size; text.alignment=alignment; text.raycastTarget=false; return text;
    }
    private static void RefreshTooltip()
    {
        if(tooltip!=null) tooltip.SetActive(hovering && cancel!=null && cancel.interactable && !VanillaModal
            && shown!=null && (shown.Phase=="Preparing" || shown.Phase=="Generating"));
    }
    private static void Relayout()
    {
        if(viewport==null || content==null) return;
        Vector2 size=viewport.rect.size;
        if(size==layoutSize && laidOutCount==visibleCount) return;
        layoutSize=size; laidOutCount=visibleCount;
        int lines=Math.Max(1,(visibleCount+Columns-1)/Columns);
        // Recompute from the current viewport: no stale sizing after a resolution/UI-scale change.
        var (fontSize,rowHeight)=Metrics(size.y);
        content.sizeDelta=new Vector2(0,Math.Max(size.y,lines*rowHeight));
        for(int i=0;i<visibleCount;i++)
        {
            var cell=(RectTransform)rows[i].transform;
            var (column,row)=Cell(i);
            cell.anchorMin=new Vector2(column/(float)Columns,1); cell.anchorMax=new Vector2((column+1)/(float)Columns,1);
            cell.pivot=new Vector2(.5f,1); cell.offsetMin=new Vector2(6,-(row+1)*rowHeight+2); cell.offsetMax=new Vector2(-6,-row*rowHeight);
            rows[i].fontSize=fontSize;
        }
    }

}

/// <summary>Only local hover presentation; generation and cancellation remain owned by the lifecycle.</summary>
internal sealed class RoadGenerationHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal Action<bool>? Changed;
    public void OnPointerEnter(PointerEventData eventData) => Changed?.Invoke(true);
    public void OnPointerExit(PointerEventData eventData) => Changed?.Invoke(false);
    private void OnDisable() => Changed?.Invoke(false);
}
