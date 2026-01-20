using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Interactable debug marker placed above road points.
/// When the player interacts, logs detailed information about how the road point's height was calculated.
/// </summary>
public class RoadPointDebugMarker : MonoBehaviour, Interactable, Hoverable
{
    public RoadSpatialGrid.RoadPointDebugInfo DebugInfo;
    public Vector2 RoadPointPosition;
    public float RoadPointHeight;
    
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    public string GetHoverName()
    {
        return "Road Point Debug";
    }

    public string GetHoverText()
    {
        float delta = DebugInfo.SmoothedHeight - DebugInfo.OriginalHeight;
        string deltaStr = delta >= 0 ? $"+{delta:F2}m" : $"{delta:F2}m";
        return $"[<color=yellow><b>$KEY_Use</b></color>] Inspect point {DebugInfo.PointIndex}/{DebugInfo.TotalPoints} ({deltaStr})";
    }

    public bool Interact(Humanoid user, bool hold, bool alt)
    {
        if (hold)
            return false;
            
        LogDebugInfo();
        return true;
    }

    public bool UseItem(Humanoid user, ItemDrop.ItemData item)
    {
        return false;
    }

    private void LogDebugInfo()
    {
        StringBuilder sb = new StringBuilder();
        
        sb.AppendLine("=== Road Point Debug ===");
        sb.AppendLine($"Position: ({RoadPointPosition.x:F1}, {RoadPointPosition.y:F1})");
        sb.AppendLine($"Index: {DebugInfo.PointIndex} of {DebugInfo.TotalPoints} points in road");
        sb.AppendLine();
        
        sb.AppendLine($"Terrain Height (blended): {DebugInfo.OriginalHeight:F2}m");
        sb.AppendLine($"Smoothed Road Height: {DebugInfo.SmoothedHeight:F2}m");
        
        float delta = DebugInfo.SmoothedHeight - DebugInfo.OriginalHeight;
        string aboveBelow = delta >= 0 ? "road above terrain" : "road below terrain";
        sb.AppendLine($"Delta: {delta:F2}m ({aboveBelow})");
        sb.AppendLine();
        
        // Smoothing window analysis
        int halfWindow = RoadConstants.HeightSmoothingWindow / 2;
        int requestedStart = DebugInfo.PointIndex - halfWindow;
        int requestedEnd = DebugInfo.PointIndex + halfWindow;
        
        sb.AppendLine("Smoothing Window:");
        sb.AppendLine($"  Requested: {RoadConstants.HeightSmoothingWindow} points (idx {requestedStart} to {requestedEnd})");
        
        bool isTruncated = DebugInfo.ActualWindowSize < RoadConstants.HeightSmoothingWindow;
        string truncationInfo;
        if (!isTruncated)
        {
            truncationInfo = "[FULL WINDOW]";
        }
        else if (DebugInfo.WindowStart == 0)
        {
            truncationInfo = "[TRUNCATED - at path start]";
        }
        else
        {
            truncationInfo = "[TRUNCATED - at path end]";
        }
        
        sb.AppendLine($"  Actual: {DebugInfo.ActualWindowSize} points (idx {DebugInfo.WindowStart} to {DebugInfo.WindowEnd}) {truncationInfo}");
        sb.AppendLine();
        
        // Heights in window
        if (DebugInfo.WindowHeights != null && DebugInfo.WindowHeights.Length > 0)
        {
            sb.AppendLine("Heights in Window:");
            int showCount = Mathf.Min(5, DebugInfo.WindowHeights.Length);
            
            StringBuilder firstLine = new StringBuilder("  First: ");
            for (int i = 0; i < showCount; i++)
            {
                firstLine.Append($"{DebugInfo.WindowHeights[i]:F1}m ");
            }
            sb.AppendLine(firstLine.ToString());
            
            if (DebugInfo.WindowHeights.Length > showCount * 2)
            {
                sb.AppendLine("  ...");
                
                StringBuilder lastLine = new StringBuilder("  Last:  ");
                int startIdx = DebugInfo.WindowHeights.Length - showCount;
                for (int i = startIdx; i < DebugInfo.WindowHeights.Length; i++)
                {
                    lastLine.Append($"{DebugInfo.WindowHeights[i]:F1}m ");
                }
                sb.AppendLine(lastLine.ToString());
            }
        }
        
        sb.AppendLine();
        
        // Compare with current WorldGenerator height (raw vs what we used)
        if (WorldGenerator.instance != null)
        {
            float rawHeight = WorldGenerator.instance.GetHeight(RoadPointPosition.x, RoadPointPosition.y);
            float blendedHeight = BiomeBlendedHeight.GetBlendedHeight(RoadPointPosition.x, RoadPointPosition.y, WorldGenerator.instance);
            
            sb.AppendLine("Height Comparison:");
            sb.AppendLine($"  Raw WorldGen height: {rawHeight:F2}m");
            sb.AppendLine($"  Biome-blended height: {blendedHeight:F2}m");
            sb.AppendLine($"  Blend difference: {blendedHeight - rawHeight:F2}m");
            sb.AppendLine($"  Road target: {DebugInfo.SmoothedHeight:F2}m");
        }
        
        string output = sb.ToString();
        
        // Log to BepInEx
        Log.LogInfo(output);
        
        // Also show in-game message
        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Road point {DebugInfo.PointIndex}: delta={delta:F2}m (see console)");
    }
}
