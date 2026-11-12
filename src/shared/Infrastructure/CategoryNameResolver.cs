using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Resolves a user/agent supplied category string to a Revit Category, independent of the
    /// Revit UI language. Order matters: exact matches always win over fuzzy ones, so an English
    /// name such as "Walls" in a Spanish Revit resolves to OST_Walls ("Muros"), never to
    /// OST_StackedWalls ("Muros de pila") through a suffix match.
    /// </summary>
    public static class CategoryNameResolver
    {
        public static Category Resolve(Document doc, string input)
        {
            if (doc == null || string.IsNullOrWhiteSpace(input)) return null;
            var text = input.Trim();

            // 1. Exact localized display name ("Muros", "Puertas", "Walls" in an English Revit).
            foreach (Category cat in doc.Settings.Categories)
            {
                if (string.Equals(cat.Name, text, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }

            // 2. Exact BuiltInCategory name, with or without the OST_ prefix, ignoring spaces,
            //    plus a naive plural ("Door" -> OST_Doors). Language independent.
            var compact = text.Replace(" ", string.Empty).Replace("_", string.Empty);
            if (compact.StartsWith("OST", StringComparison.OrdinalIgnoreCase))
                compact = compact.Substring(3);

            foreach (var candidate in new[] { compact, compact + "s", compact + "es" })
            {
                var found = TryGet(doc, "OST_" + candidate);
                if (found != null) return found;
            }

            // 3. Fuzzy suffix match, accepted only when it is unambiguous.
            var matches = new List<Category>();
            foreach (BuiltInCategory value in Enum.GetValues(typeof(BuiltInCategory)))
            {
                var name = value.ToString();
                if (!name.EndsWith(compact, StringComparison.OrdinalIgnoreCase)) continue;
                var cat = TryGet(doc, value);
                if (cat != null && matches.All(m => RevitCompat.GetId(m.Id) != RevitCompat.GetId(cat.Id)))
                    matches.Add(cat);
            }
            return matches.Count == 1 ? matches[0] : null;
        }

        private static Category TryGet(Document doc, string builtInName)
        {
            return Enum.TryParse<BuiltInCategory>(builtInName, true, out var bic) ? TryGet(doc, bic) : null;
        }

        private static Category TryGet(Document doc, BuiltInCategory bic)
        {
            try { return Category.GetCategory(doc, bic); } catch { return null; }
        }
    }
}
