using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RvtMcp.Plugin.Handlers
{
    /// <summary>Shared helpers for the office-standards tools (inventory / import between open documents).</summary>
    internal static class StandardsSupport
    {
        /// <summary>
        /// Finds an open, non-linked document by title (with or without extension, case-insensitive).
        /// Empty title returns the active document.
        /// </summary>
        public static Document FindOpenDocument(UIApplication app, string title, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(title))
            {
                var active = app.ActiveUIDocument?.Document;
                if (active == null) error = "No document is open.";
                return active;
            }

            var wanted = Normalize(title);
            var titles = new List<string>();
            foreach (Document d in app.Application.Documents)
            {
                if (d == null || d.IsLinked) continue;
                titles.Add(d.Title);
                if (Normalize(d.Title) == wanted || Normalize(SafePathName(d)) == wanted)
                    return d;
            }

            error = "No open document titled '" + title + "'. Open documents: " + string.Join(", ", titles)
                    + ". Open the template/project in Revit first (it may stay in the background).";
            return null;
        }

        public static double? FeetToMm(Parameter p)
        {
            if (p == null || p.StorageType != StorageType.Double) return null;
            return Math.Round(p.AsDouble() * 304.8, 2);
        }

        public static string String(Parameter p) => p == null ? null : (p.AsString() ?? p.AsValueString());

        public static int? Int(Parameter p) => p == null || p.StorageType != StorageType.Integer ? (int?)null : p.AsInteger();

        public static double? Double(Parameter p) => p == null || p.StorageType != StorageType.Double ? (double?)null : Math.Round(p.AsDouble(), 3);

        private static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var t = s.Trim();
            var ext = Path.GetExtension(t);
            if (!string.IsNullOrEmpty(ext) && (ext.Equals(".rvt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".rte", StringComparison.OrdinalIgnoreCase)))
                t = t.Substring(0, t.Length - ext.Length);
            return t.ToLowerInvariant();
        }

        private static string SafePathName(Document d)
        {
            try { return string.IsNullOrEmpty(d.PathName) ? d.Title : Path.GetFileName(d.PathName); }
            catch { return d.Title; }
        }
    }
}
