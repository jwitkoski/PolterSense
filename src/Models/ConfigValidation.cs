using System.Text.RegularExpressions;

namespace PolterSense.Models;

public static class ConfigValidation
{
    public const int MaxBytes = 512 * 1024;
    private static readonly Regex IdPattern = new("^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{3}([0-9a-fA-F]{3})?$", RegexOptions.CultureInvariant);
    private static readonly Regex BssidPattern = new("^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$", RegexOptions.CultureInvariant);

    public static void Validate(Config config)
    {
        Require(config != null && config.SchemaVersion == 1, "Only scene schema version 1 is supported.");
        Text(config!.Name);
        Require(config.Map != null, "Map settings are required.");
        Require(double.IsFinite(config.Map!.WallThickness) && config.Map.WallThickness is >= 0 and <= 10, "Invalid wall thickness.");
        Require(config.Map.WallOpacity == null || (double.IsFinite(config.Map.WallOpacity.Value) && config.Map.WallOpacity is >= 0 and <= 1), "Invalid wall opacity.");
        Color(config.Map.WallColor);
        Collection(config.Floors, 32); Collection(config.Wisps, 256); Collection(config.Sources, 256); Collection(config.Links, 1024);
        var floorIds = Ids(config.Floors.Select(f => f.Id));
        var wispIds = Ids(config.Wisps.Select(w => w.Id));
        var sourceIds = Ids(config.Sources.Select(s => s.Id));
        Ids(config.Links.Select(l => l.Id));
        var roomIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var floor in config.Floors)
        {
            Text(floor.Name); PointValue(floor.Elevation);
            Collection(floor.Bounds, 2);
            Require(floor.Bounds.Length == 2, "Floor bounds require two XYZ points.");
            Point(floor.Bounds[0], 3); Point(floor.Bounds[1], 3);
            Require(Enumerable.Range(0, 3).All(i => floor.Bounds[0][i] < floor.Bounds[1][i]), "Floor bounds must have positive extent on each axis.");
            Require(floor.Elevation >= floor.Bounds[0][2] && floor.Elevation <= floor.Bounds[1][2], "Floor elevation must lie within its Z bounds.");
            Collection(floor.Rooms, 128);
            foreach (var room in floor.Rooms)
            {
                Id(room.Id); Require(roomIds.Add(room.Id), "Room IDs must be unique throughout the building.");
                Text(room.Name); Color(room.Color); Polygon(room.Points, floor.Bounds);
            }
        }
        foreach (var wisp in config.Wisps)
        {
            Text(wisp.Name); OptionalText(wisp.ExternalId); Placement(wisp.Point, wisp.Floors, wisp.Orientation, floorIds);
        }
        foreach (var source in config.Sources)
        {
            Text(source.Name); OptionalText(source.ApLabel); OptionalText(source.RadioId);
            Require(source.Bssid == null || BssidPattern.IsMatch(source.Bssid), "Source BSSID must be a colon-separated radio address or absent.");
            Require(source.Channel == null || source.Channel is >= 1 and <= 233, "Invalid source channel.");
            Placement(source.Point, source.Floors, source.Orientation, floorIds);
        }
        var pairs = new HashSet<(string, string)>();
        foreach (var link in config.Links)
        {
            Text(link.Name);
            Require(link.WispId != null && wispIds.Contains(link.WispId) && link.SourceId != null && sourceIds.Contains(link.SourceId), "Sensing link references an unknown Wisp or Source.");
            Require(pairs.Add((link.WispId!, link.SourceId!)), "Only one sensing link per Wisp and Source pair is supported.");
        }
    }

    private static void Placement(double[] point, string[] floors, double[]? orientation, HashSet<string> ids)
    {
        Point(point, 3); Collection(floors, 32);
        Require(floors.All(f => f != null && ids.Contains(f)) && floors.Distinct(StringComparer.Ordinal).Count() == floors.Length, "Placement references an unknown or repeated floor.");
        if (orientation != null) Point(orientation, 3);
    }

    private static void Polygon(double[][] points, double[][] bounds)
    {
        Collection(points, 256);
        Require(points.Length >= 3, "Room polygons require at least three points.");
        foreach (var point in points)
        {
            Point(point, 2);
            Require(point[0] >= bounds[0][0] && point[0] <= bounds[1][0] && point[1] >= bounds[0][1] && point[1] <= bounds[1][1], "Room polygon extends beyond floor bounds.");
        }
        // Existing formats may explicitly repeat the first vertex to close the polygon.
        var n = points[0].SequenceEqual(points[^1]) ? points.Length - 1 : points.Length;
        Require(n >= 3, "Room polygons require three distinct vertices.");
        var area = 0.0;
        for (var i = 0; i < n; i++)
        {
            var a = points[i]; var b = points[(i + 1) % n];
            Require(!a.SequenceEqual(b), "Room polygons cannot contain zero-length edges.");
            area += a[0] * b[1] - b[0] * a[1];
            for (var j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                Require(!Intersects(a, b, points[j], points[(j + 1) % n]), "Room polygons cannot intersect themselves.");
            }
        }
        Require(Math.Abs(area) > 1e-8, "Room polygons must enclose nonzero area.");
    }

    private static bool Intersects(double[] a, double[] b, double[] c, double[] d)
    {
        static double Cross(double[] p, double[] q, double[] r) => (q[0] - p[0]) * (r[1] - p[1]) - (q[1] - p[1]) * (r[0] - p[0]);
        static bool On(double[] p, double[] q, double[] r) => r[0] >= Math.Min(p[0], q[0]) && r[0] <= Math.Max(p[0], q[0]) && r[1] >= Math.Min(p[1], q[1]) && r[1] <= Math.Max(p[1], q[1]);
        var x = Cross(a, b, c); var y = Cross(a, b, d); var z = Cross(c, d, a); var w = Cross(c, d, b);
        return (x * y < 0 && z * w < 0) || (x == 0 && On(a, b, c)) || (y == 0 && On(a, b, d)) || (z == 0 && On(c, d, a)) || (w == 0 && On(c, d, b));
    }

    private static HashSet<string> Ids(IEnumerable<string> values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values) { Id(value); Require(result.Add(value), "Inventory IDs must be unique within their collection."); }
        return result;
    }
    private static void Id(string value) => Require(value != null && IdPattern.IsMatch(value), "IDs must contain 1-64 letters, digits, dots, hyphens or underscores and start with a letter or digit.");
    private static void Text(string value) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl), "Display names must contain 1-200 printable characters.");
    private static void OptionalText(string? value) { if (value != null) Text(value); }
    private static void Color(string? value) => Require(value == null || ColorPattern.IsMatch(value), "Colors must be hexadecimal RGB values.");
    private static void Collection<T>(T[] values, int max) => Require(values != null && values.Length <= max && values.All(v => v != null), "Configuration collection is null, oversized or contains null entries.");
    private static void Point(double[] point, int dimension) { Collection(point, dimension); Require(point.Length == dimension, "Coordinate dimension is invalid."); foreach (var value in point) PointValue(value); }
    private static void PointValue(double value) => Require(double.IsFinite(value) && Math.Abs(value) <= 100000, "Coordinates must be finite building-frame values within 100000 meters.");
    private static void Require(bool condition, string message) { if (!condition) throw new ConfigValidationException(message); }
}
