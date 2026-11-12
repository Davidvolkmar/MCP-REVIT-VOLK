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
    /// Copies standards (dimension/text/spot types, annotation families, view templates, fill
    /// patterns, line styles...) from another open document into the ACTIVE document, like a
    /// targeted "Transfer Project Standards". dry_run (default true) only reports what would be
    /// copied and which names already exist. Name conflicts keep the destination's types, so
    /// existing standards are never overwritten.
    /// </summary>
    public class ImportStandardsFromDocumentHandler : IRevitCommand
    {
        public string Name => "import_standards_from_document";
        public string Description => "Copy chosen standards (types, tags, view templates, patterns) from another open document into the active document.";
        public string ParametersSchema => @"{
  ""type"": ""object"",
  ""required"": [""source_document_title"", ""element_ids""],
  ""properties"": {
    ""source_document_title"": { ""type"": ""string"" },
    ""element_ids"": { ""type"": ""array"", ""items"": { ""type"": ""integer"" } },
    ""dry_run"": { ""type"": ""boolean"" }
  }
}";

        public CommandResult Execute(UIApplication app, string paramsJson)
        {
            JObject request;
            try { request = string.IsNullOrWhiteSpace(paramsJson) ? new JObject() : JObject.Parse(paramsJson); }
            catch (JsonException ex) { return CommandResult.Fail("Parameters must be a JSON object: " + ex.Message); }

            var dest = app.ActiveUIDocument?.Document;
            if (dest == null) return CommandResult.Fail("No active document.");

            var source = StandardsSupport.FindOpenDocument(app, request.Value<string>("source_document_title"), out var srcError);
            if (source == null) return CommandResult.Fail(srcError);
            if (source.Equals(dest)) return CommandResult.Fail("Source and destination are the same document. Activate the destination model first.");

            var idsToken = request["element_ids"] as JArray;
            if (idsToken == null || idsToken.Count == 0) return CommandResult.Fail("element_ids must list at least one element id from the source document.");
            if (idsToken.Count > 500) return CommandResult.Fail("element_ids cannot exceed 500 per call.");
            bool dryRun = request.Value<bool?>("dry_run") ?? true;

            var ids = new List<ElementId>();
            var items = new JArray();
            var rejected = new JArray();
            foreach (var t in idsToken)
            {
                long raw;
                try { raw = t.Value<long>(); } catch { rejected.Add(new JObject { ["id"] = t, ["error"] = "not an integer" }); continue; }
                if (!RevitCompat.CanRepresentElementId(raw)) { rejected.Add(new JObject { ["id"] = raw, ["error"] = RevitCompat.ElementIdRangeError(raw) }); continue; }
                var e = source.GetElement(RevitCompat.ToElementId(raw));
                if (e == null) { rejected.Add(new JObject { ["id"] = raw, ["error"] = "not found in source document" }); continue; }
                if (e is View v && !v.IsTemplate) { rejected.Add(new JObject { ["id"] = raw, ["name"] = e.Name, ["error"] = "only view templates can be copied, not views" }); continue; }

                ids.Add(e.Id);
                items.Add(new JObject
                {
                    ["source_id"] = raw,
                    ["name"] = e.Name,
                    ["kind"] = e.GetType().Name,
                    ["exists_in_destination"] = ExistsByName(dest, e)
                });
            }

            var result = new JObject
            {
                ["dry_run"] = dryRun,
                ["source_document"] = source.Title,
                ["destination_document"] = dest.Title,
                ["items"] = items,
                ["rejected"] = rejected,
                ["duplicate_name_policy"] = "keep destination types (never overwrite)"
            };

            if (dryRun || ids.Count == 0)
            {
                result["copied"] = 0;
                result["next_step"] = ids.Count == 0 ? "Nothing valid to copy." : "Re-run with dry_run=false to copy.";
                return CommandResult.Ok(result);
            }

            using (var tx = new Transaction(dest, "RvtMcp: import standards"))
            {
                tx.Start();
                try
                {
                    var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new KeepDestinationTypes());
                    var copied = ElementTransformUtils.CopyElements(source, ids, dest, Transform.Identity, options);
                    var status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        return CommandResult.Fail("Transaction did not commit: " + status);

                    result["copied"] = copied?.Count ?? 0;
                    result["new_ids"] = new JArray((copied ?? new List<ElementId>()).Select(i => RevitCompat.GetId(i)));
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("Copy failed and was rolled back: " + ex.Message);
                }
            }
        }

        private static bool ExistsByName(Document dest, Element e)
        {
            try
            {
                var type = e.GetType();
                if (e is FamilySymbol fs)
                {
                    return new FilteredElementCollector(dest).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .Any(s => s.FamilyName == fs.FamilyName && s.Name == fs.Name);
                }
                return new FilteredElementCollector(dest).OfClass(type).Any(x => x.Name == e.Name);
            }
            catch { return false; }
        }

        private sealed class KeepDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
                => DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
