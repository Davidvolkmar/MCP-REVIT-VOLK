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
    /// Read-only inventory of the annotation/graphic standards of any open document (project or
    /// template): dimension and spot types, text types, annotation symbols/tags, view templates,
    /// fill patterns, line styles, grid and level types. Used to compare office templates and to
    /// pick the element ids that import_standards_from_document copies into the active model.
    /// </summary>
    public class InventoryAnnotationStandardsHandler : IRevitCommand
    {
        public string Name => "inventory_annotation_standards";
        public string Description => "Inventory annotation and graphic standards of an open document.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""properties"": {
    ""document_title"": { ""type"": ""string"" },
    ""sections"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""dimension_types"", ""text_types"", ""annotation_symbols"", ""view_templates"", ""fill_patterns"", ""line_styles"", ""grid_level_types""] } },
    ""name_contains"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var doc = StandardsSupport.FindOpenDocument(app, request.Value<string>("document_title"), out var docError);
            if (doc == null) return CommandResult.Fail(docError);

            var sections = (request["sections"] as JArray)?.Select(t => t.ToString()).ToList();
            bool Want(string s) => sections == null || sections.Count == 0 || sections.Contains(s, StringComparer.OrdinalIgnoreCase);
            var filter = request.Value<string>("name_contains");
            int limit = Math.Max(1, Math.Min(request.Value<int?>("limit") ?? 300, 1000));
            bool Match(string name) => string.IsNullOrEmpty(filter) || (name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            var result = new JObject
            {
                ["document"] = doc.Title,
                ["path"] = SafeString(() => doc.PathName),
                ["is_active"] = doc.Equals(app.ActiveUIDocument?.Document),
                ["limit_per_section"] = limit
            };
            var truncated = new JArray();

            if (Want("dimension_types"))
            {
                var items = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                    .Where(t => Match(t.Name))
                    .OrderBy(t => t.StyleType.ToString()).ThenBy(t => t.Name)
                    .Select(t =>
                    {
                        var o = new JObject
                        {
                            ["id"] = RevitCompat.GetId(t.Id),
                            ["name"] = t.Name,
                            ["family"] = SafeString(() => t.FamilyName),
                            ["style"] = t.StyleType.ToString(),
                            ["font"] = StandardsSupport.String(t.get_Parameter(BuiltInParameter.TEXT_FONT)),
                            ["text_size_mm"] = StandardsSupport.FeetToMm(t.get_Parameter(BuiltInParameter.TEXT_SIZE)),
                            ["bold"] = StandardsSupport.Int(t.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)) == 1,
                            ["width_factor"] = StandardsSupport.Double(t.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)),
                            ["line_weight"] = StandardsSupport.Int(t.get_Parameter(BuiltInParameter.LINE_PEN))
                        };
                        try
                        {
                            var fo = t.GetUnitsFormatOptions();
                            o["units"] = fo.UseDefault ? "project default" : fo.GetUnitTypeId().TypeId + " accuracy " + fo.Accuracy;
                        }
                        catch { }
                        return o;
                    }).ToList();
                result["dimension_types"] = Cap(items, limit, "dimension_types", truncated);
            }

            if (Want("text_types"))
            {
                var items = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>()
                    .Where(t => Match(t.Name)).OrderBy(t => t.Name)
                    .Select(t => (JToken)new JObject
                    {
                        ["id"] = RevitCompat.GetId(t.Id),
                        ["name"] = t.Name,
                        ["font"] = StandardsSupport.String(t.get_Parameter(BuiltInParameter.TEXT_FONT)),
                        ["text_size_mm"] = StandardsSupport.FeetToMm(t.get_Parameter(BuiltInParameter.TEXT_SIZE)),
                        ["bold"] = StandardsSupport.Int(t.get_Parameter(BuiltInParameter.TEXT_STYLE_BOLD)) == 1,
                        ["width_factor"] = StandardsSupport.Double(t.get_Parameter(BuiltInParameter.TEXT_WIDTH_SCALE)),
                        ["transparent_background"] = StandardsSupport.Int(t.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)) == 1
                    }).ToList();
                result["text_types"] = Cap(items, limit, "text_types", truncated);
            }

            if (Want("annotation_symbols"))
            {
                var groups = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                    .Where(s => s.Category != null && s.Category.CategoryType == CategoryType.Annotation)
                    .Where(s => Match(s.FamilyName) || Match(s.Name))
                    .GroupBy(s => s.Category.Name).OrderBy(g => g.Key)
                    .Select(g => (JToken)new JObject
                    {
                        ["category"] = g.Key,
                        ["built_in"] = SafeString(() => ((BuiltInCategory)RevitCompat.GetId(g.First().Category.Id)).ToString()),
                        ["count"] = g.Count(),
                        ["types"] = new JArray(g.OrderBy(s => s.FamilyName).ThenBy(s => s.Name).Take(limit)
                            .Select(s => new JObject { ["id"] = RevitCompat.GetId(s.Id), ["family"] = s.FamilyName, ["type"] = s.Name }))
                    }).ToList();
                result["annotation_symbols"] = new JArray(groups);
            }

            if (Want("view_templates"))
            {
                var items = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => v.IsTemplate && Match(v.Name)).OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name)
                    .Select(v => (JToken)new JObject
                    {
                        ["id"] = RevitCompat.GetId(v.Id),
                        ["name"] = v.Name,
                        ["view_type"] = v.ViewType.ToString(),
                        ["scale"] = SafeInt(() => v.Scale),
                        ["detail_level"] = SafeString(() => v.DetailLevel.ToString()),
                        ["display_style"] = SafeString(() => v.DisplayStyle.ToString())
                    }).ToList();
                result["view_templates"] = Cap(items, limit, "view_templates", truncated);
            }

            if (Want("fill_patterns"))
            {
                var items = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .Where(f => Match(f.Name)).OrderBy(f => f.Name)
                    .Select(f => (JToken)new JObject
                    {
                        ["id"] = RevitCompat.GetId(f.Id),
                        ["name"] = f.Name,
                        ["target"] = SafeString(() => f.GetFillPattern().Target.ToString()),
                        ["solid"] = SafeBool(() => f.GetFillPattern().IsSolidFill)
                    }).ToList();
                result["fill_patterns"] = Cap(items, limit, "fill_patterns", truncated);
            }

            if (Want("line_styles"))
            {
                var lines = SafeCategory(doc, BuiltInCategory.OST_Lines);
                var items = new List<JToken>();
                if (lines != null)
                {
                    foreach (Category sub in lines.SubCategories)
                    {
                        if (!Match(sub.Name)) continue;
                        var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        items.Add(new JObject
                        {
                            ["id"] = gs == null ? (JToken)null : RevitCompat.GetId(gs.Id),
                            ["name"] = sub.Name,
                            ["line_weight"] = SafeInt(() => sub.GetLineWeight(GraphicsStyleType.Projection) ?? 0),
                            ["color"] = SafeString(() => sub.LineColor.IsValid ? sub.LineColor.Red + "," + sub.LineColor.Green + "," + sub.LineColor.Blue : null)
                        });
                    }
                }
                result["line_styles"] = Cap(items.OrderBy(i => (string)i["name"]).ToList(), limit, "line_styles", truncated);
            }

            if (Want("grid_level_types"))
            {
                result["grid_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(GridType)).Cast<GridType>()
                    .Where(t => Match(t.Name)).Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }));
                result["level_types"] = new JArray(new FilteredElementCollector(doc).OfClass(typeof(LevelType)).Cast<LevelType>()
                    .Where(t => Match(t.Name)).Select(t => new JObject { ["id"] = RevitCompat.GetId(t.Id), ["name"] = t.Name }));
            }

            result["truncated_sections"] = truncated;
            result["next_step"] = "Copy chosen ids into the active model with import_standards_from_document (dry_run first).";
            return CommandResult.Ok(result);
        }

        private static JArray Cap(List<JToken> items, int limit, string section, JArray truncated)
        {
            if (items.Count > limit) truncated.Add(section + " (" + items.Count + ")");
            return new JArray(items.Take(limit));
        }

        private static JArray Cap(List<JObject> items, int limit, string section, JArray truncated)
            => Cap(items.Cast<JToken>().ToList(), limit, section, truncated);

        private static Category SafeCategory(Document doc, BuiltInCategory bic)
        {
            try { return Category.GetCategory(doc, bic); } catch { return null; }
        }

        private static JToken SafeString(Func<string> f) { try { return f(); } catch { return null; } }
        private static JToken SafeInt(Func<int> f) { try { return f(); } catch { return null; } }
        private static JToken SafeBool(Func<bool> f) { try { return f(); } catch { return null; } }
    }
}
