using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>A part of an item's text that links somewhere else than the item: a type's name, to its definition.</summary>
public sealed record InfoViewLink(int Start, int Length, Location Location);

/// <summary>
/// The links of the type names in the info view's items: each name the view writes a type or context by (see
/// <see cref="NyxCompiler.Signatures.typeNamesAt"/>) links to the type's definition where an item's code writes it as
/// a whole name (<c>Point</c> in <c>#some(Point)</c>, not in <c>Point.new</c>), except where it is a tag's name
/// (after <c>#</c>), a record field's name (before <c>:</c>), or part of the item's own <see cref="InfoViewItem.Name"/>.
/// </summary>
public static partial class InfoViewTypeLinks
{
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*")]
    private static partial Regex WrittenName();

    /// <summary>The links in <paramref name="item"/>'s text, and in those of the items under it, to the types <paramref name="types"/> names.</summary>
    public static InfoViewItem WithLinks(InfoViewItem item, IReadOnlyDictionary<string, Location> types) =>
        item with
        {
            Links = LinksIn(item, types) is { Count: > 0 } links ? links : null,
            Children = item.Children?.Select(child => WithLinks(child, types)).ToList()
        };

    private static List<InfoViewLink> LinksIn(InfoViewItem item, IReadOnlyDictionary<string, Location> types)
    {
        var text = item.Text;
        var links = new List<InfoViewLink>();
        foreach (Match match in WrittenName().Matches(text))
        {
            var start = match.Index;
            var end = start + match.Length;
            var inName = item.Name is { } name && start < name.Start + name.Length && name.Start < end;
            var isTag = start > 0 && text[start - 1] == '#';
            var next = end;
            while (next < text.Length && text[next] == ' ')
            {
                next++;
            }

            var isField = next < text.Length && text[next] == ':';
            if (!inName && !isTag && !isField && types.TryGetValue(match.Value, out var location))
            {
                links.Add(new InfoViewLink(start, match.Length, location));
            }
        }

        return links;
    }
}
