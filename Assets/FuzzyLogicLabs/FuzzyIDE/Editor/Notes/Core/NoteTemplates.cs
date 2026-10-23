using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Notes.Core
{
    /// <summary>Values available to template variables. Filled by the UI layer (git branch, scene, selection...).</summary>
    public sealed class TemplateContext
    {
        public DateTime Now = DateTime.Now;
        public string Title = "";
        public string Branch = "";
        public string Scene = "";
        public string Selection = "";
        public string Project = "";
    }

    /// <summary>Options for daily notes; the UI builds them from <see cref="NoteSettings"/>.</summary>
    public sealed class DailyNoteOptions
    {
        public string Folder = "Daily";
        public string DateFormat = "yyyy-MM-dd";
        /// <summary>Name/link of a template note; empty uses the built-in daily template.</summary>
        public string Template = "";
    }

    /// <summary>
    /// Expands <c>{{variable}}</c> placeholders: <c>{{date}}</c>, <c>{{date:dddd, d MMMM}}</c>, <c>{{time}}</c>,
    /// <c>{{datetime}}</c>, <c>{{title}}</c>, <c>{{branch}}</c>, <c>{{scene}}</c>, <c>{{selection}}</c>, <c>{{project}}</c>.
    /// Unknown variables are left untouched.
    /// </summary>
    public static class NoteTemplates
    {
        public const string DefaultDailyTemplate =
            "# {{date:dddd, d MMMM yyyy}}\n\n" +
            "## Focus\n- [ ] \n\n" +
            "## Notes\n\n" +
            "## Log\n- 🌿 `{{branch}}` · 🎬 {{scene}}\n";

        private static readonly Regex VariableRx = new Regex(@"\{\{\s*([A-Za-z]+)(?::([^}]*))?\s*\}\}", RegexOptions.Compiled);

        public static string Apply(string template, TemplateContext ctx)
        {
            if (string.IsNullOrEmpty(template)) return template ?? "";
            ctx = ctx ?? new TemplateContext();

            return VariableRx.Replace(template, m =>
            {
                string name = m.Groups[1].Value.ToLowerInvariant();
                string format = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                switch (name)
                {
                    case "date": return Format(ctx.Now, format ?? "yyyy-MM-dd");
                    case "time": return Format(ctx.Now, format ?? "HH:mm");
                    case "datetime": return Format(ctx.Now, format ?? "yyyy-MM-dd HH:mm");
                    case "title": return ctx.Title ?? "";
                    case "branch": return ctx.Branch ?? "";
                    case "scene": return ctx.Scene ?? "";
                    case "selection": return ctx.Selection ?? "";
                    case "project": return ctx.Project ?? "";
                    default: return m.Value;
                }
            });
        }

        private static string Format(DateTime value, string format)
        {
            try { return value.ToString(format, CultureInfo.CurrentCulture); }
            catch (FormatException) { return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        }
    }
}
