using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Places one spot elevation (level mark, e.g. "N: 0.00") per room in a plan view, on the top
    /// face of the floor under the room, offset from the room point by a paper distance so it does
    /// not collide with the room tag. Rooms without a floor underneath are reported, not guessed.
    /// </summary>
    public class CreateRoomSpotElevationsHandler : IRevitCommand
    {
        private const double MmPerFoot = 304.8;

        public string Name => "create_room_spot_elevations";
        public string Description => "Place a spot elevation on the floor of every room in a plan view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""view_id"": { ""type"": ""integer"" },
    ""spot_type_id"": { ""type"": ""integer"" },
    ""offset_x_paper_mm"": { ""type"": ""number"" },
    ""offset_y_paper_mm"": { ""type"": ""number"" },
    ""clearance_paper_mm"": { ""type"": ""number"" },
    ""room_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""dry_run"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("No document is open.");
            JObject req;
            try { req = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            View view = doc.ActiveView;
            var viewId = req.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value)) return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));
                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
            }
            if (!(view is ViewPlan) || view.IsTemplate) return CommandResult.Fail("A non-template plan view is required (active view or view_id).");

            SpotDimensionType spotType = null;
            var spotTypeId = req.Value<long?>("spot_type_id");
            if (spotTypeId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(spotTypeId.Value)) return CommandResult.Fail("spot_type_id " + RevitCompat.ElementIdRangeError(spotTypeId.Value));
                spotType = doc.GetElement(RevitCompat.ToElementId(spotTypeId.Value)) as SpotDimensionType;
                if (spotType == null || spotType.StyleType != DimensionStyleType.SpotElevation)
                    return CommandResult.Fail("spot_type_id must be a spot ELEVATION type.");
            }

            double scale = Math.Max(1, view.Scale);
            double dx = (req.Value<double?>("offset_x_paper_mm") ?? 0) * scale / MmPerFoot;
            double dy = (req.Value<double?>("offset_y_paper_mm") ?? -14.0) * scale / MmPerFoot;
            bool dryRun = req.Value<bool?>("dry_run") ?? false;
            var onlyIds = (req["room_ids"] as JArray)?.Select(t => t.Value<long>()).ToHashSet();

            var rooms = new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>()
                .Where(r => r.Area > 0 && r.Location is LocationPoint)
                .Where(r => onlyIds == null || onlyIds.Contains(RevitCompat.GetId(r.Id)))
                .ToList();

            var floorFaces = new List<Tuple<Reference, Face, Element>>();
            foreach (var f in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_Floors).WhereElementIsNotElementType())
            {
                if (!(f is HostObject host)) continue;
                IList<Reference> tops;
                try { tops = HostObjectUtils.GetTopFaces(host); } catch { continue; }
                foreach (var r in tops)
                    if (f.GetGeometryObjectFromReference(r) is Face face) floorFaces.Add(Tuple.Create(r, face, f));
            }

            // Obstacles in plan: wall bodies and grid lines visible in the view.
            var wallLines = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>()
                .Select(w => Tuple.Create((w.Location as LocationCurve)?.Curve, w.Width / 2.0))
                .Where(t => t.Item1 != null).ToList();
            var gridLines = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                .Select(g => g.Curve).Where(c => c != null).ToList();
            double clearance = (req.Value<double?>("clearance_paper_mm") ?? 3.0) * scale / MmPerFoot;
            double u = scale / MmPerFoot; // one paper mm in feet
            // Candidate offsets around the room tag (paper mm): requested/default first, then below,
            // right, left, above, then farther out.
            var candidates = new List<XYZ> { new XYZ(dx, dy, 0) };
            foreach (var c in new[] { new[] { 0.0, -14 }, new[] { 0.0, -20 }, new[] { 16.0, -4 }, new[] { -16.0, -4 }, new[] { 0.0, 10 }, new[] { 16.0, -14 }, new[] { -16.0, -14 }, new[] { 0.0, -6 } })
                candidates.Add(new XYZ(c[0] * u, c[1] * u, 0));

            var plan = new JArray(); var skipped = new JArray();
            var work = new List<Tuple<Room, Reference, XYZ>>();
            foreach (var room in rooms)
            {
                var rp = ((LocationPoint)room.Location).Point;
                double probeZ = (room.Level?.Elevation ?? rp.Z) + 0.5;
                XYZ p = null; string placement = null;
                foreach (var off in candidates)
                {
                    var q = rp + off;
                    bool inside; try { inside = room.IsPointInRoom(new XYZ(q.X, q.Y, probeZ)); } catch { inside = false; }
                    if (!inside) continue;
                    var q2 = new XYZ(q.X, q.Y, 0);
                    bool clearOfWalls = wallLines.All(w => DistanceXY(w.Item1, q2) - w.Item2 >= clearance);
                    bool clearOfGrids = gridLines.All(g => DistanceXY(g, q2) >= clearance * 0.75);
                    if (clearOfWalls && clearOfGrids) { p = q; placement = Math.Round(off.X / u) + "," + Math.Round(off.Y / u) + " paper mm"; break; }
                }
                if (p == null) { p = rp + new XYZ(dx, dy, 0); placement = "fallback (no clear spot found)"; }
                Tuple<Reference, XYZ, Element> hit = null;
                foreach (var ff in floorFaces)
                {
                    IntersectionResult ir = null;
                    try { ir = ff.Item2.Project(p); } catch { }
                    if (ir == null) continue;
                    // Project() also returns points for faces far above/below; keep the floor nearest the room level.
                    if (hit == null || Math.Abs(ir.XYZPoint.Z - p.Z) < Math.Abs(hit.Item2.Z - p.Z)) hit = Tuple.Create(ff.Item1, ir.XYZPoint, ff.Item3);
                }
                var rid = RevitCompat.GetId(room.Id);
                if (hit == null) { skipped.Add(new JObject { ["room_id"] = rid, ["room"] = room.Name, ["reason"] = "no floor top face under the mark position" }); continue; }
                plan.Add(new JObject { ["room_id"] = rid, ["room"] = room.Name, ["floor_id"] = RevitCompat.GetId(hit.Item3.Id), ["elevation_mm"] = Math.Round(hit.Item2.Z * MmPerFoot, 1), ["placement"] = placement });
                work.Add(Tuple.Create(room, hit.Item1, hit.Item2));
            }

            var result = new JObject { ["view_id"] = RevitCompat.GetId(view.Id), ["dry_run"] = dryRun, ["planned"] = plan, ["skipped"] = skipped };
            if (dryRun || work.Count == 0) { result["created"] = new JArray(); return CommandResult.Ok(result); }

            var created = new JArray(); var errors = new JArray();
            using (var tx = new Transaction(doc, "RvtMcp: room spot elevations"))
            {
                tx.Start();
                foreach (var w in work)
                {
                    try
                    {
                        var o = w.Item3;
                        var bend = o + new XYZ(0.5, 0.5, 0);
                        var end = o + new XYZ(1.0, 0.5, 0);
                        var spot = doc.Create.NewSpotElevation(view, w.Item2, o, bend, end, o, false);
                        if (spot == null) throw new Exception("NewSpotElevation returned null");
                        if (spotType != null) spot.ChangeTypeId(spotType.Id);
                        created.Add(new JObject { ["room_id"] = RevitCompat.GetId(w.Item1.Id), ["spot_id"] = RevitCompat.GetId(spot.Id) });
                    }
                    catch (Exception ex) { errors.Add(new JObject { ["room_id"] = RevitCompat.GetId(w.Item1.Id), ["error"] = ex.Message }); }
                }
                if (created.Count == 0) { tx.RollBack(); return CommandResult.Fail("No spot elevation could be created: " + errors.ToString(Formatting.None)); }
                var st = tx.Commit();
                if (st != TransactionStatus.Committed) return CommandResult.Fail("Transaction did not commit: " + st);
            }
            result["created"] = created; result["errors"] = errors;
            return CommandResult.Ok(result);
        }

        private static double DistanceXY(Curve c, XYZ p)
        {
            try
            {
                var flat = c is Line l
                    ? (Curve)Line.CreateBound(new XYZ(l.GetEndPoint(0).X, l.GetEndPoint(0).Y, 0), new XYZ(l.GetEndPoint(1).X, l.GetEndPoint(1).Y, 0))
                    : c;
                return flat.Distance(p);
            }
            catch { return double.MaxValue; }
        }
    }
}
