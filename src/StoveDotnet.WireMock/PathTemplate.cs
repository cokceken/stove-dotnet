using System.Text;
using System.Text.RegularExpressions;
using WireMock.Matchers;
using WireMock.RequestBuilders;

namespace StoveDotnet.WireMock;

/// <summary>
/// An OpenAPI-style path template such as <c>/buckets/{bucket}/objects/{key}</c>. <c>{name}</c> matches one path segment;
/// <c>{name+}</c> matches the rest of the path including slashes (e.g. S3 object keys). A path without placeholders
/// matches exactly. Matching ignores case.
/// </summary>
public sealed partial class PathTemplate
{
    private static readonly Dictionary<string, PathTemplate> Cache = new(StringComparer.Ordinal);
    private readonly Regex _regex;

    private PathTemplate(string template)
    {
        Template = template;
        Pattern = BuildPattern(template, out var parameterNames);
        ParameterNames = parameterNames;
        _regex = new Regex(Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Template { get; }

    /// <summary>The anchored regular expression the template compiles to.</summary>
    public string Pattern { get; }

    public IReadOnlyList<string> ParameterNames { get; }

    public static PathTemplate Parse(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        lock (Cache)
        {
            if (!Cache.TryGetValue(template, out var parsed))
            {
                parsed = new PathTemplate(template);
                Cache[template] = parsed;
            }

            return parsed;
        }
    }

    public bool IsMatch(string? path) => path is not null && _regex.IsMatch(path);

    /// <summary>Extracts placeholder values from <paramref name="path"/>; returns false when it does not match.</summary>
    public bool TryMatch(string? path, out IReadOnlyDictionary<string, string> parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        parameters = values;
        if (path is null)
        {
            return false;
        }

        var match = _regex.Match(path);
        if (!match.Success)
        {
            return false;
        }

        foreach (var name in ParameterNames)
        {
            values[name] = Uri.UnescapeDataString(match.Groups[GroupName(name)].Value);
        }

        return true;
    }

    public override string ToString() => Template;

    private static string BuildPattern(string template, out List<string> parameterNames)
    {
        parameterNames = [];
        var pattern = new StringBuilder("^");
        var position = 0;
        foreach (Match placeholder in Placeholder().Matches(template))
        {
            pattern.Append(Regex.Escape(template[position..placeholder.Index]));
            var name = placeholder.Groups["name"].Value;
            var greedy = placeholder.Groups["greedy"].Success;
            parameterNames.Add(name);
            pattern.Append("(?<").Append(GroupName(name)).Append('>').Append(greedy ? ".+" : "[^/]+").Append(')');
            position = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(template[position..])).Append('$');
        return pattern.ToString();
    }

    // Regex group names must be identifiers; OpenAPI parameter names may contain '-' or '.'.
    private static string GroupName(string parameterName) =>
        "p_" + string.Concat(parameterName.Select(c => char.IsLetterOrDigit(c) ? c.ToString() : $"_{(int)c}_"));

    [GeneratedRegex(@"\{(?<name>[^{}+]+)(?<greedy>\+)?\}")]
    private static partial Regex Placeholder();
}

public static class PathTemplateRequestBuilderExtensions
{
    /// <summary>Matches requests whose path fits an OpenAPI-style template, e.g. <c>/stock/{productId}</c>.</summary>
    public static IRequestBuilder WithPathTemplate(this IRequestBuilder builder, string template)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return (IRequestBuilder)builder.WithPath(new RegexMatcher(PathTemplate.Parse(template).Pattern, ignoreCase: true));
    }
}
