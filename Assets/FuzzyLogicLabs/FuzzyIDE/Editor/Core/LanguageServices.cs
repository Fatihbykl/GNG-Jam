using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FuzzyLogicLabs.FuzzyIDE.Editor.Roslyn;

namespace FuzzyLogicLabs.FuzzyIDE.Editor.Core
{
    using ColorSpan = SyntaxSpan;

    public interface ILanguageService
    {
        bool SupportsAutoComplete { get; }
        Task ParseCode(string code, CancellationToken cancellationToken = default);
        List<ColorSpan> GetSyntaxHighlights();
        Task<List<ColorSpan>> GetSyntaxHighlightsAsync(CancellationToken cancellationToken = default);
        Task<HoverInfoResult> GetHoverInfoAsync(string code, int position);
    }

    public class CSharpLanguageService : ILanguageService
    {
        private RoslynAnalyzerService _roslynService;
        private string _filePath;
        private IEnumerable<string> _references;

        public bool SupportsAutoComplete => true;

        public CSharpLanguageService(string filePath, IEnumerable<string> references)
        {
            _roslynService = new RoslynAnalyzerService();
            _filePath = filePath;
            _references = references;
        }

        public RoslynAnalyzerService Roslyn => _roslynService;

        public async Task ParseCode(string code, CancellationToken cancellationToken = default)
        {
            await _roslynService.UpdateCodeAsync(code, _filePath, _references, cancellationToken);
        }

        public Task<List<ColorSpan>> GetSyntaxHighlightsAsync(CancellationToken cancellationToken = default)
        {
            return _roslynService.GetSyntaxHighlightsAsync(cancellationToken);
        }

        public List<ColorSpan> GetSyntaxHighlights()
        {
            // Fallback synchronous getter
            return _roslynService.GetSyntaxHighlightsAsync().GetAwaiter().GetResult();
        }

        public Task<HoverInfoResult> GetHoverInfoAsync(string code, int position)
        {
            return _roslynService.GetHoverInfoAsync(code, position);
        }
    }

    public abstract class RegexLanguageService : ILanguageService
    {
        public bool SupportsAutoComplete => false;
        protected List<ColorSpan> _highlights = new List<ColorSpan>();

        public async Task ParseCode(string code, CancellationToken cancellationToken = default)
        {
            var highlights = new List<ColorSpan>();
            await Task.Run(() =>
            {
                RunRegexAnalysis(code, highlights);
            });
            _highlights = highlights;
        }

        public List<ColorSpan> GetSyntaxHighlights()
        {
            return _highlights;
        }

        public Task<List<ColorSpan>> GetSyntaxHighlightsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_highlights);
        }

        public Task<HoverInfoResult> GetHoverInfoAsync(string code, int position)
        {
            return Task.FromResult<HoverInfoResult>(null);
        }

        protected abstract void RunRegexAnalysis(string code, List<ColorSpan> highlights);
    }

    public class JsonLanguageService : RegexLanguageService
    {
        private static readonly Regex s_StringRegex = new Regex(@"""(\\.|[^""])*""", RegexOptions.Compiled);
        private static readonly Regex s_NumberRegex = new Regex(@"\b-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?\b", RegexOptions.Compiled);
        private static readonly Regex s_BoolRegex = new Regex(@"\b(true|false|null)\b", RegexOptions.Compiled);
        private static readonly Regex s_KeyRegex = new Regex(@"""(\\.|[^""])*""\s*:", RegexOptions.Compiled);

        protected override void RunRegexAnalysis(string code, List<ColorSpan> highlights)
        {
            if (string.IsNullOrEmpty(code)) return;

            foreach (Match m in s_NumberRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#B5CEA8")); // Light Green

            foreach (Match m in s_BoolRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#569CD6")); // Blue

            foreach (Match m in s_StringRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#D69D85")); // Orange

            foreach (Match m in s_KeyRegex.Matches(code))
            {
                int quoteLength = m.Value.LastIndexOf('"') + 1;
                highlights.Add(new ColorSpan(m.Index, quoteLength, "#9CDCFE")); // Light Blue
            }
        }
    }

    public class ShaderLanguageService : RegexLanguageService
    {
        private static readonly Regex s_KeywordRegex = new Regex(@"\b(Shader|Properties|SubShader|Pass|CGPROGRAM|ENDCG|CGINCLUDE|FallBack|CustomEditor|float|float2|float3|float4|half|half2|half3|half4|fixed|fixed2|fixed3|fixed4|sampler2D|struct|return|out|in|inout|void)\b", RegexOptions.Compiled);
        private static readonly Regex s_StringRegex = new Regex(@"""(\\.|[^""])*""", RegexOptions.Compiled);
        private static readonly Regex s_CommentRegex = new Regex(@"//.*|/\*[\s\S]*?\*/", RegexOptions.Compiled);

        protected override void RunRegexAnalysis(string code, List<ColorSpan> highlights)
        {
            if (string.IsNullOrEmpty(code)) return;

            foreach (Match m in s_KeywordRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#569CD6")); // Blue

            foreach (Match m in s_StringRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#D69D85")); // Orange

            foreach (Match m in s_CommentRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#57A64A")); // Green
        }
    }

    public class XmlLanguageService : RegexLanguageService
    {
        private static readonly Regex s_TagRegex = new Regex(@"</?([a-zA-Z0-9_:-]+)\b[^>]*>", RegexOptions.Compiled);
        private static readonly Regex s_AttrRegex = new Regex(@"\b([a-zA-Z0-9_:-]+)\s*=", RegexOptions.Compiled);
        private static readonly Regex s_StringRegex = new Regex(@"""(\\.|[^""])*""", RegexOptions.Compiled);

        protected override void RunRegexAnalysis(string code, List<ColorSpan> highlights)
        {
            if (string.IsNullOrEmpty(code)) return;

            foreach (Match m in s_StringRegex.Matches(code))
                highlights.Add(new ColorSpan(m.Index, m.Length, "#D69D85")); // Orange

            foreach (Match m in s_TagRegex.Matches(code))
            {
                Group tagGroup = m.Groups[1];
                if (tagGroup.Success)
                    highlights.Add(new ColorSpan(tagGroup.Index, tagGroup.Length, "#569CD6")); // Blue
                
                foreach (Match attrM in s_AttrRegex.Matches(m.Value))
                {
                    Group attrGroup = attrM.Groups[1];
                    if (attrGroup.Success)
                        highlights.Add(new ColorSpan(m.Index + attrGroup.Index, attrGroup.Length, "#9CDCFE")); // Light Blue
                }
            }
        }
    }

    public class PlaintextLanguageService : ILanguageService
    {
        public bool SupportsAutoComplete => false;

        public Task ParseCode(string code, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public List<ColorSpan> GetSyntaxHighlights()
        {
            return new List<ColorSpan>();
        }

        public Task<List<ColorSpan>> GetSyntaxHighlightsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<ColorSpan>());
        }

        public Task<HoverInfoResult> GetHoverInfoAsync(string code, int position)
        {
            return Task.FromResult<HoverInfoResult>(null);
        }
    }
}
