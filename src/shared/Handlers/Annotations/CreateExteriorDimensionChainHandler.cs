using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>
    /// Office-style exterior dimension chain for one side of a plan view, built from the model:
    ///   tier "openings": outward wall faces at facade steps/corners + door/window edges,
    ///   tier "overall":  outermost faces of the side,
    ///   tier "grids":    grid-to-grid (grids perpendicular to the side).
    /// Tiers are placed outside the building at paper distances (scaled by the view scale), so
    /// nobody hand-computes coordinates. v1 handles orthogonal (axis-aligned) straight walls.
    /// </summary>
    public class CreateExteriorDimensionChainHandler : IRevitCommand
    {
        private const double MmPerFoot = 304.8;

        public string Name => "create_exterior_dimension_chain";
        public string Description => "Create the exterior dimension chain (openings / overall / grids tiers) on one side of a plan view.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""side""],
  ""properties"": {
    ""side"": { ""type"": ""string"", ""enum"": [""top"", ""bottom"", ""left"", ""right""] },
    ""view_id"": { ""type"": ""integer"" },
    ""tiers"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""openings"", ""overall"", ""grids""] } },
    ""first_offset_paper_mm"": { ""type"": ""number"" },
    ""tier_spacing_paper_mm"": { ""type"": ""number"" },
    ""dimension_type_id"": { ""type"": ""integer"" },
    ""grid_dimension_type_id"": { ""type"": ""integer"" },
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

            var side = (req.Value<string>("side") ?? "").Trim().ToLowerInvariant();
            if (side != "top" && side != "bottom" && side != "left" && side != "right")
                return CommandResult.Fail("side must be top, bottom, left or right.");

            View view = doc.ActiveView;
            var viewId = req.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                if (!RevitCompat.CanRepresentElementId(viewId.Value)) return CommandResult.Fail("view_id " + RevitCompat.ElementIdRangeError(viewId.Value));
                view = doc.GetElement(RevitCompat.ToElementId(viewId.Value)) as View;
            }
            if (!(view is ViewPlan) || view.IsTemplate) return CommandResult.Fail("A non-template plan view is required (active view or view_id).");

            var tiers = (req["tiers"] as JArray)?.Select(t => t.ToString().ToLowerInvariant()).ToList()
                        ?? new List<string> { "openings", "overall", "grids" };
            double scale = Math.Max(1, view.Scale);
            double firstOffset = (req.Value<double?>("first_offset_paper_mm") ?? 4.0) * scale / MmPerFoot;
            double spacing = (req.Value<double?>("tier_spacing_paper_mm") ?? 7.0) * scale / MmPerFoot;
            bool dryRun = req.Value<bool?>("dry_run") ?? false;

            DimensionType dimType = ResolveDimType(doc, req.Value<long?>("dimension_type_id"), out var dtErr);
            if (dtErr != null) return CommandResult.Fail(dtErr);
            DimensionType gridDimType = ResolveDimType(doc, req.Value<long?>("grid_dimension_type_id"), out var gdtErr) ?? dimType;
            if (gdtErr != null) return CommandResult.Fail(gdtErr);

            // Side geometry: "outward" is the side normal; "along" is the measuring direction.
            XYZ outward = side == "top" ? XYZ.BasisY : side == "bottom" ? -XYZ.BasisY : side == "right" ? XYZ.BasisX : -XYZ.BasisX;
            XYZ along = (side == "top" || side == "bottom") ? XYZ.BasisX : XYZ.BasisY;
            Func<XYZ, double> O = p => p.DotProduct(outward);
            Func<XYZ, double> A = p => p.DotProduct(along);

            var walls = new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>()
                .Select(w => new WallInfo(w)).Where(w => w.Line != null).ToList();
            if (walls.Count == 0) return CommandResult.Fail("No straight walls are visible in the view.");

            var skipped = new JArray();
            foreach (var w in walls.Where(w => !w.IsAxisAligned)) skipped.Add(new JObject { ["wall_id"] = RevitCompat.GetId(w.Wall.Id), ["reason"] = "not axis-aligned (v1 handles orthogonal walls only)" });
            walls = walls.Where(w => w.IsAxisAligned).ToList();

            // Facade walls on this side: parallel to "along", and no other parallel wall further out
            // overlapping the same span.
            var parallel = walls.Where(w => Math.Abs(w.Direction.DotProduct(along)) > 0.999).ToList();
            var perpendicular = walls.Where(w => Math.Abs(w.Direction.DotProduct(along)) < 0.001).ToList();
            // A wall is facade when part of its span (>= ~0.25 m) is not hidden behind a parallel wall
            // further out, so stepped facades (bumps, setbacks) keep their recessed segments.
            var exposed = new Dictionary<WallInfo, List<double[]>>();
            foreach (var w in parallel)
            {
                var free = new List<double[]> { new[] { w.SpanMin(A), w.SpanMax(A) } };
                foreach (var o in parallel.Where(o => o != w && O(o.Mid) > O(w.Mid) + 1e-3))
                    free = Subtract(free, o.SpanMin(A), o.SpanMax(A));
                free = free.Where(iv => iv[1] - iv[0] >= 0.8).ToList();
                if (free.Count > 0) exposed[w] = free;
            }
            var facade = exposed.Keys.ToList();
            if (facade.Count == 0) return CommandResult.Fail("No facade walls found on side " + side + ".");

            double outerEdge = walls.Max(w => w.OuterExtent(O));
            double spanMin = facade.Min(w => w.SpanMin(A)) - 1e-6, spanMax = facade.Max(w => w.SpanMax(A)) + 1e-6;

            // Perpendicular walls that close the facade: they meet an END of a facade wall that lies
            // on its exposed part (corners and steps), not partitions landing on hidden segments.
            var closing = perpendicular.Where(p => facade.Any(f => TouchesExposedEnd(p, f, exposed[f], A))).ToList();
            double centreA = walls.Average(w => A(w.Mid));

            var planned = new JArray();
            var tierRefs = new List<Tuple<string, ReferenceArray, List<string>, DimensionType>>();

            if (tiers.Contains("openings"))
            {
                var refs = new ReferenceArray(); var labels = new List<string>(); var used = new List<double>();
                foreach (var p in closing)
                    AddFace(refs, labels, used, p, A, centreA, "wall " + RevitCompat.GetId(p.Wall.Id) + " outer face");
                foreach (var f in facade)
                {
                    foreach (var inst in new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                                 .Where(i => i.Host != null && i.Host.Id == f.Wall.Id))
                    {
                        foreach (var rt in new[] { FamilyInstanceReferenceType.Left, FamilyInstanceReferenceType.Right })
                        {
                            var r = SafeRefs(inst, rt).FirstOrDefault();
                            if (r != null) { refs.Append(r); labels.Add(inst.Symbol?.Name + " " + rt.ToString().ToLowerInvariant() + " edge"); }
                        }
                    }
                }
                tierRefs.Add(Tuple.Create("openings", refs, labels, dimType));
            }

            if (tiers.Contains("overall"))
            {
                var refs = new ReferenceArray(); var labels = new List<string>(); var used = new List<double>();
                var ends = closing.Count >= 2 ? closing : perpendicular;
                var minWall = ends.OrderBy(w => A(w.Mid)).FirstOrDefault();
                var maxWall = ends.OrderByDescending(w => A(w.Mid)).FirstOrDefault();
                if (minWall != null) AddFace(refs, labels, used, minWall, A, centreA, "wall " + RevitCompat.GetId(minWall.Wall.Id) + " outer face", towardsMin: true);
                if (maxWall != null && maxWall != minWall) AddFace(refs, labels, used, maxWall, A, centreA, "wall " + RevitCompat.GetId(maxWall.Wall.Id) + " outer face", towardsMin: false);
                tierRefs.Add(Tuple.Create("overall", refs, labels, dimType));
            }

            if (tiers.Contains("grids"))
            {
                var refs = new ReferenceArray(); var labels = new List<string>();
                foreach (var g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>()
                             .Where(g => g.Curve is Line gl && Math.Abs(gl.Direction.DotProduct(along)) < 0.001)
                             .OrderBy(g => A(((Line)g.Curve).Origin)))
                {
                    refs.Append(new Reference(g)); labels.Add("grid " + g.Name);
                }
                tierRefs.Add(Tuple.Create("grids", refs, labels, gridDimType));
            }

            // Lay the tiers out from the building outward, skipping tiers with < 2 references.
            int k = 0;
            var lines = new List<Tuple<string, Line, ReferenceArray, DimensionType>>();
            foreach (var t in tierRefs)
            {
                var info = new JObject { ["tier"] = t.Item1, ["references"] = new JArray(t.Item3) };
                if (t.Item2.Size < 2) { info["skipped"] = "fewer than 2 references"; planned.Add(info); continue; }
                if (t.Item1 == "openings")
                {
                    // A flat facade without openings gives the same two faces as the overall tier.
                    var overall = tierRefs.FirstOrDefault(x => x.Item1 == "overall");
                    if (overall != null && overall.Item2.Size == t.Item2.Size && t.Item3.All(l => overall.Item3.Contains(l)))
                    { info["skipped"] = "same references as the overall tier (no openings or steps on this side)"; planned.Add(info); continue; }
                }
                double o = outerEdge + firstOffset + k * spacing;
                double z = view.GenLevel?.Elevation ?? 0;
                var p0 = along * spanMin + outward * o + XYZ.BasisZ * z;
                var p1 = along * spanMax + outward * o + XYZ.BasisZ * z;
                lines.Add(Tuple.Create(t.Item1, Line.CreateBound(p0, p1), t.Item2, t.Item4));
                info["offset_from_facade_mm"] = Math.Round((firstOffset + k * spacing) * MmPerFoot);
                planned.Add(info);
                k++;
            }

            var result = new JObject
            {
                ["side"] = side,
                ["view_id"] = RevitCompat.GetId(view.Id),
                ["view_scale"] = view.Scale,
                ["dry_run"] = dryRun,
                ["facade_wall_ids"] = new JArray(facade.Select(f => RevitCompat.GetId(f.Wall.Id))),
                ["tiers"] = planned,
                ["skipped_walls"] = skipped
            };
            if (dryRun || lines.Count == 0) { result["created"] = new JArray(); return CommandResult.Ok(result); }

            var created = new JArray(); var errors = new JArray();
            using (var tx = new Transaction(doc, "RvtMcp: exterior dimension chain"))
            {
                tx.Start();
                foreach (var l in lines)
                {
                    try
                    {
                        var d = doc.Create.NewDimension(view, l.Item2, l.Item3);
                        if (d == null) throw new Exception("NewDimension returned null");
                        if (l.Item4 != null) d.ChangeTypeId(l.Item4.Id);
                        created.Add(new JObject { ["tier"] = l.Item1, ["dimension_id"] = RevitCompat.GetId(d.Id), ["segments"] = d.NumberOfSegments == 0 ? 1 : d.NumberOfSegments });
                    }
                    catch (Exception ex) { errors.Add(new JObject { ["tier"] = l.Item1, ["error"] = ex.Message }); }
                }
                if (created.Count == 0) { tx.RollBack(); result["errors"] = errors; return CommandResult.Fail("No dimension could be created: " + errors.ToString(Formatting.None)); }
                var st = tx.Commit();
                if (st != TransactionStatus.Committed) return CommandResult.Fail("Transaction did not commit: " + st);
            }
            result["created"] = created;
            result["errors"] = errors;
            return CommandResult.Ok(result);
        }

        private static DimensionType ResolveDimType(Document doc, long? id, out string error)
        {
            error = null;
            if (!id.HasValue) return null;
            if (!RevitCompat.CanRepresentElementId(id.Value)) { error = "dimension type id " + RevitCompat.ElementIdRangeError(id.Value); return null; }
            var t = doc.GetElement(RevitCompat.ToElementId(id.Value)) as DimensionType;
            if (t == null) error = "Id " + id.Value + " is not a DimensionType.";
            return t;
        }

        private static IList<Reference> SafeRefs(FamilyInstance i, FamilyInstanceReferenceType t)
        {
            try { return i.GetReferences(t) ?? new List<Reference>(); } catch { return new List<Reference>(); }
        }

        private static double Overlap(double a0, double a1, double b0, double b1) => Math.Min(a1, b1) - Math.Max(a0, b0);

        /// <summary>
        /// True when an end of the perpendicular wall meets an END of the facade wall that lies on the
        /// facade's exposed part (a corner or facade step), not a partition landing mid-facade.
        /// </summary>
        private static bool TouchesExposedEnd(WallInfo perp, WallInfo facade, List<double[]> exposedSpans, Func<XYZ, double> a)
        {
            double tol = Math.Max(perp.Thickness, facade.Thickness) + 0.05;
            foreach (var fe in new[] { facade.Line.GetEndPoint(0), facade.Line.GetEndPoint(1) })
            {
                double fa = a(fe);
                if (!exposedSpans.Any(iv => fa >= iv[0] - tol && fa <= iv[1] + tol)) continue;
                foreach (var pe in new[] { perp.Line.GetEndPoint(0), perp.Line.GetEndPoint(1) })
                    if (new XYZ(pe.X - fe.X, pe.Y - fe.Y, 0).GetLength() <= tol) return true;
            }
            return false;
        }

        /// <summary>Removes [c0, c1] from a set of disjoint intervals.</summary>
        private static List<double[]> Subtract(List<double[]> intervals, double c0, double c1)
        {
            var result = new List<double[]>();
            foreach (var iv in intervals)
            {
                if (c1 <= iv[0] || c0 >= iv[1]) { result.Add(iv); continue; }
                if (c0 > iv[0]) result.Add(new[] { iv[0], c0 });
                if (c1 < iv[1]) result.Add(new[] { c1, iv[1] });
            }
            return result;
        }

        /// <summary>Adds the side face of a perpendicular wall that faces away from the building along the measuring axis.</summary>
        private static void AddFace(ReferenceArray refs, List<string> labels, List<double> used, WallInfo w, Func<XYZ, double> A, double centreA, string label, bool? towardsMin = null)
        {
            Reference best = null; double bestPos = 0; bool found = false;
            foreach (var shell in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                foreach (var r in SafeSide(w.Wall, shell))
                {
                    var face = w.Wall.GetGeometryObjectFromReference(r) as PlanarFace;
                    if (face == null) continue;
                    double pos = A(face.Origin);
                    // Default: keep the face farther from the building centre along the axis.
                    bool better = !found
                        || (towardsMin == true && pos < bestPos)
                        || (towardsMin == false && pos > bestPos)
                        || (towardsMin == null && Math.Abs(pos - centreA) > Math.Abs(bestPos - centreA));
                    if (better) { best = r; bestPos = pos; found = true; }
                }
            }
            if (best == null || used.Any(u => Math.Abs(u - bestPos) < 0.003)) return;
            used.Add(bestPos); refs.Append(best); labels.Add(label);
        }

        private static IList<Reference> SafeSide(Wall w, ShellLayerType t)
        {
            try { return HostObjectUtils.GetSideFaces(w, t) ?? new List<Reference>(); } catch { return new List<Reference>(); }
        }

        private sealed class WallInfo
        {
            public Wall Wall; public Line Line; public XYZ Direction; public XYZ Mid; public double Thickness;

            public WallInfo(Wall w)
            {
                Wall = w;
                Line = (w.Location as LocationCurve)?.Curve as Line;
                if (Line == null) return;
                Direction = new XYZ(Line.Direction.X, Line.Direction.Y, 0).Normalize();
                Mid = Line.Evaluate(0.5, true);
                Thickness = w.Width;
            }

            public bool IsAxisAligned => Math.Abs(Direction.X) > 0.999 || Math.Abs(Direction.Y) > 0.999;
            public double SpanMin(Func<XYZ, double> a) => Math.Min(a(Line.GetEndPoint(0)), a(Line.GetEndPoint(1)));
            public double SpanMax(Func<XYZ, double> a) => Math.Max(a(Line.GetEndPoint(0)), a(Line.GetEndPoint(1)));
            public double OuterExtent(Func<XYZ, double> o) => Math.Max(o(Line.GetEndPoint(0)), o(Line.GetEndPoint(1))) + Thickness / 2.0;
        }
    }
}
