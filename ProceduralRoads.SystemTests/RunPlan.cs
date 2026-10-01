using System.Globalization;
using Valheim.Testing.Game;

namespace ProceduralRoads.SystemTests;

// A Roads system-test plan: the toolkit's pinned dedicated-server plan plus the Roads scenarios and their fields.
public sealed class RunPlan : ServerRunPlan
{
    public const string SessionTokenVariable = "ROADS_TEST_SESSION_TOKEN";
    public const string SessionCapability = "roads.testing/session";
    public static readonly string[] RequiredPlugins = ["warpalicious.ProceduralRoads", "valheimCLI.valheimCLI", "testing.proceduralroads.adapter"];
    /// <summary>A terrain-persistence client measures what a stock client sees: it must pin these <c>absent</c>.</summary>
    public static readonly string[] ClientMustBeAbsent = ["warpalicious.ProceduralRoads", "warpalicious.More_World_Locations_AIO"];
    public string Append { get; set; } = "";
    public List<ZoneExpectation> Expected { get; set; } = [];
    /// <summary>The client a terrain-persistence run measures from (owned or attached); required for that scenario's run mode.</summary>
    public ClientRunPlan? Client { get; set; }
    /// <summary>The prepare-terrain run's client plans, pinned by full path and SHA256; required with the client.</summary>
    public PinnedFile? HeightPlan { get; set; }
    public PinnedFile? PaintPlan { get; set; }
    public static RunPlan Read(string path) => Read<RunPlan>(path);
    public void Validate()
    {
        RequireScenario("empty-save", "bridge-respawn", "terrain-calibration", "terrain-persistence");
        if (Scenario == "terrain-calibration") RequireEnvironmentFlag("ROADS_TEST_TERRAIN_CALIBRATION", "let the adapter write the calibration terrain");
        if (Scenario == "terrain-persistence") RequireEnvironmentFlag("ROADS_TEST_PERSISTENT_TERRAIN", "let the adapter write the persistent terrain fixture");
        PersistentTerrainScenario.WidthFor(this); // Refuses unknown paint profiles before launch.
        ValidateServerPlan(RequiredPlugins, SessionTokenVariable);
        OnlyForScenario("append and expected", Append.Length != 0 || Expected.Count != 0, "bridge-respawn");
        OnlyForScenario("client, heightPlan and paintPlan", Client != null || HeightPlan != null || PaintPlan != null, "terrain-persistence");
        if (Scenario == "bridge-respawn")
        {
            if (!Append.StartsWith("road_path ", StringComparison.Ordinal) || Append.IndexOfAny(['\r', '\n', ';']) >= 0)
                throw new ArgumentException("Supply one road_path append command.");
            var pairs = Append[10..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (pairs.Length < 2 || pairs.Any(p => p.Split(',').Length != 2 || p.Split(',').Any(n => !double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))))
                throw new ArgumentException("road_path requires finite X,Z pairs.");
            if (Expected.Count == 0 || Expected.Sum(x => x.Pieces.Count) == 0 || Expected.Select(x => (x.X, x.Z)).Distinct().Count() != Expected.Count)
                throw new ArgumentException("Freeze independent expectations for distinct bridge zones.");
            foreach (var zone in Expected)
            {
                // Beyond +/-255 the game files every zone under one shared slot, so a census there would read the wrong objects.
                if (Math.Abs((long)zone.X) > 255 || Math.Abs((long)zone.Z) > 255 || zone.Pieces.Count > 512) throw new ArgumentException("Invalid census zone or size.");
                TransformMatch.Match(zone.Pieces, zone.Pieces, .05, 1); // Validate transform shape/finite values before launch.
            }
        }
        if (Client != null)
        {
            Client.Validate(ClientMustBeAbsent);
            (HeightPlan ?? throw new ArgumentException("Pin the prepared client height plan.")).Validate("client height plan");
            (PaintPlan ?? throw new ArgumentException("Pin the prepared client paint plan.")).Validate("client paint plan");
        }
        else if (HeightPlan != null || PaintPlan != null) throw new ArgumentException("Client plans supplied without a client.");
    }
    /// <summary>The server's world UID pin, which the joined client must also report.</summary>
    public string WorldUid => Pins["worlduid"];
}
