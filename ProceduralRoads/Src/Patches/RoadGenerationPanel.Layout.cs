using System.Globalization;
using System.Text;

namespace ProceduralRoads;

/// <summary>The panel's pure layout rules. No engine types, so the unit tests compile this file.</summary>
internal static partial class RoadGenerationPanel
{
    internal const int Columns = 2;

    /// <summary>Islands alternate between the two columns: slot 0 left, 1 right, 2 left, and so on. One island
    /// left is the same layout with an empty right column.</summary>
    internal static (int Column, int Row) Cell(int index) => (index % Columns, index / Columns);

    /// <summary>Text size and row height depend only on the viewport height, never on how many islands are
    /// active, so a row does not change size as islands finish. A resolution or UI-scale change still re-lays out.</summary>
    internal static (float FontSize, float RowHeight) Metrics(float viewportHeight)
    {
        float font = System.Math.Max(12f, System.Math.Min(20f, viewportHeight / 30f));
        return (font, font * 3.6f + 14f);
    }

    /// <summary>Settled places needed before the remaining-time estimate is shown.</summary>
    internal const int EstimateAfter = 3;

    /// <summary>What the island is doing, in words. The internal stage names stay (tests and the adapter read them);
    /// only the display changes: Pricing is "Planning routes", Connecting is "Building roads", Fitting is "Fitting terrain".</summary>
    internal static string StageLabel(RoadIslandProgress row) => row.Stage switch
    {
        "Pricing" => $"Planning routes {row.PricesCompleted}/{row.PricesTotal}",
        "Connecting" => "Building roads",
        "Fitting" => "Fitting terrain",
        "Preparing" => "Preparing",
        _ => row.Stage,
    };

    private static readonly string[] BiomePrefixes =
        { "Meadows_", "BlackForest_", "Swamp_", "Mountain_", "Plains_", "Mistlands_", "Ashlands_", "DeepNorth_", "Ocean_" };

    /// <summary>A prefab name made readable: a leading biome prefix goes, other underscores become spaces and camel
    /// case is split ("Mistlands_DvergrBossEntrance1" becomes "Dvergr Boss Entrance1", "Hildir_cave" "Hildir cave",
    /// "Mistlands_GuardTower3_ruined_new" "Guard Tower3 ruined new"; "Crypt4" stays).</summary>
    internal static string FriendlyName(string prefab)
    {
        string name = prefab;
        foreach (string prefix in BiomePrefixes)
            if (name.Length > prefix.Length && name.StartsWith(prefix, System.StringComparison.Ordinal)) { name = name.Substring(prefix.Length); break; }
        var text = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '_') { text.Append(' '); continue; }
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }
        return text.ToString();
    }

    internal static string Duration(double seconds)
    {
        int total = (int)seconds;
        return total >= 60 ? $"{total / 60}m {total % 60:00}s" : $"{total}s";
    }

    /// <summary>Average time per settled place times the places left; null until <see cref="EstimateAfter"/> places are
    /// settled. Timed from the start of connecting, so the pricing stage (when no place settles) does not inflate it.</summary>
    internal static double? RemainingSeconds(RoadIslandProgress row) =>
        row.Processed >= EstimateAfter ? row.SettlingSeconds / row.Processed * System.Math.Max(0, row.Destinations - row.Processed) : null;

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    /// <summary>Fraction of the island's places settled, for the progress bar.</summary>
    internal static float Fraction(RoadIslandProgress row) =>
        row.Destinations <= 0 ? 0f : System.Math.Min(1f, row.Processed / (float)row.Destinations);

    /// <summary>The three text lines of one island's row. Pure: formatted only when the owner publishes a new snapshot.</summary>
    internal static string RowText(RoadIslandProgress row)
    {
        var c = CultureInfo.InvariantCulture;
        string doing = StageLabel(row);
        if (row.Destination != null && row.Stage == "Connecting")
            doing += $" to {FriendlyName(row.Destination)} ({row.DestinationMetres.ToString("N0", c)} m)";
        string places = $"{row.Processed} of {row.Destinations} places · {row.Reached} reached · {row.Unreached} unreached";
        var built = new StringBuilder(row.Kilometres.ToString("F1", c)).Append(" km");
        if (row.Bridges > 0) built.Append(" · ").Append(Count(row.Bridges, "bridge", "bridges"));
        if (row.Staircases > 0) built.Append(" · ").Append(Count(row.Staircases, "staircase", "staircases"));
        double? left = RemainingSeconds(row);
        built.Append(" · ").Append(Duration(row.IslandSeconds)).Append(" · ")
            .Append(left == null ? "— left" : left.Value < 60 ? "under 1m left" : $"~{(int)(left.Value / 60 + .5)}m left");
        return $"Island {row.Island} · {doing}\n<size=85%>{places}\n{built}</size>";
    }
}
