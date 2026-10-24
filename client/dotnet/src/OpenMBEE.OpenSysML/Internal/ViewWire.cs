using System.Collections.Generic;
using System.Linq;

namespace OpenMBEE.OpenSysML.Internal;

/// <summary>Reads a <c>RenderViewResponse</c> into a <see cref="RenderedView"/>, keeping every field and an absent message absent.</summary>
internal static class ViewWire
{
    /// <summary>Reads a rendering.</summary>
    /// <exception cref="TransportException">A node repeats an id, or names as its parent no node before it.</exception>
    internal static RenderedView Rendered(Wire.RenderViewResponse response, string method)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var nodes = new List<ViewNode>(response.Nodes.Count);
        foreach (var (node, i) in response.Nodes.Select((node, i) => (node, i)))
        {
            if (node.Parent.Length > 0 && !seen.Contains(node.Parent))
            {
                throw Malformed(method, $"nodes[{i}] names {ValueText.Quoted(node.Parent)} as its parent, which no node before it is");
            }

            if (!seen.Add(node.Id))
            {
                throw Malformed(method, $"nodes[{i}] repeats the id {ValueText.Quoted(node.Id)}");
            }

            nodes.Add(new ViewNode(
                node.Id,
                node.Kind,
                node.Name,
                node.NameSynthesized,
                node.Type,
                node.Detail,
                node.Text,
                node.StandIn,
                node.Parent,
                node.Ports.Select(port => new ViewPort(port.Id, port.Name, port.Type, port.Direction)),
                Span(node.Origin),
                node.Geometry is { } geometry
                    ? new ViewGeometry(geometry.X, geometry.Y, geometry.Width, geometry.Height, geometry.HasSize, geometry.Collapsed, geometry.Unplaced)
                    : null,
                Style(node.Style)));
        }

        return new RenderedView(
            response.View,
            response.Kind,
            response.Stated,
            nodes,
            response.Edges.Select(edge => new ViewEdge(
                edge.From,
                edge.To,
                edge.FromPort,
                edge.ToPort,
                edge.Label,
                edge.Name,
                edge.Kind,
                Span(edge.Origin),
                edge.Route.Select(point => new ViewPoint(point.X, point.Y)),
                Style(edge.Style))),
            response.Columns,
            response.Rows.Select(row => new ViewRow(row.Cells, Span(row.Origin))),
            response.Canvas is { } canvas ? new ViewCanvas(canvas.Unit, canvas.Width, canvas.Height, canvas.HasSize) : null,
            response.Notes.Select(note => new ViewNote(note.Text, note.Anchor, note.EdgeFrom, note.EdgeTo, note.X, note.Y, note.Width, note.Height, note.HasSize, Span(note.Origin))),
            response.Notices,
            response.Shown);
    }

    private static SourceSpan? Span(Wire.Span? span) =>
        span is null ? null : new SourceSpan(span.File, span.StartLine, span.StartCol, span.EndLine, span.EndCol);

    private static ViewStyle? Style(Wire.RenderStyle? style) =>
        style is null ? null : new ViewStyle(style.Fill, style.Line, style.Text, style.Font, style.FontSize, style.Bold, style.Italic);

    private static TransportException Malformed(string method, string reason) =>
        new(StatusCode.Internal, $"{method} answered a malformed rendering: {reason}.", method);
}
