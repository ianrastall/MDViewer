using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MDViewer.Services;

public sealed record MarkdownReflowResult(
    string Markdown,
    int ChangedHeadingCount,
    IReadOnlyList<string> Warnings);

internal sealed record MarkdownParts(string? FrontMatter, string Body);

public sealed class MarkdownFormatterService
{
    public string FormatAndLint(string rawMarkdown)
    {
        ArgumentNullException.ThrowIfNull(rawMarkdown);

        MarkdownParts parts = SplitFrontMatter(rawMarkdown);
        MarkdownPipeline pipeline = CreatePipeline();

        try
        {
            // Parse into Markdig's abstract syntax tree so the cleanup can mutate semantic nodes
            // instead of relying on brittle text substitutions over Pandoc output.
            MarkdownDocument document = Markdown.Parse(parts.Body, pipeline);

            NormalizeHeadingsToAtx(document);
            RemoveSyntheticHeadingReferenceDefinitions(document);

            return ReattachFrontMatter(parts.FrontMatter, RenderNormalizedMarkdown(document, pipeline));
        }
        catch (ArgumentException)
        {
            // Markdig's normalize renderer can reject pathological inline nesting, usually from
            // malformed Pandoc output or very large tables. Showing the unformatted Markdown is
            // preferable to letting an import/open command terminate the app.
            return rawMarkdown;
        }
    }

    public MarkdownReflowResult ReflowHeadings(string rawMarkdown)
    {
        ArgumentNullException.ThrowIfNull(rawMarkdown);

        MarkdownParts parts = SplitFrontMatter(rawMarkdown);
        MarkdownPipeline pipeline = CreatePipeline();
        MarkdownDocument document = Markdown.Parse(parts.Body, pipeline);
        IReadOnlyList<HeadingChange> headingChanges = NormalizeHeadingHierarchy(document);
        RemoveSyntheticHeadingReferenceDefinitions(document);

        string markdown = ReattachFrontMatter(parts.FrontMatter, RenderNormalizedMarkdown(document, pipeline));
        IReadOnlyList<string> warnings = BuildReflowWarnings(rawMarkdown, headingChanges);

        return new MarkdownReflowResult(
            markdown,
            headingChanges.Count(change => change.OriginalLevel != change.NormalizedLevel),
            warnings);
    }

    private static MarkdownPipeline CreatePipeline()
    {
        // Build the same broad parser profile that Markdig recommends for modern Markdown.
        // The pipeline is immutable after Build() and could be cached later if formatting becomes hot.
        return new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
    }

    private static MarkdownParts SplitFrontMatter(string markdown)
    {
        string normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new MarkdownParts(null, markdown);
        }

        int closingStart = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);

        if (closingStart < 0)
        {
            return new MarkdownParts(null, markdown);
        }

        int bodyStart = closingStart + "\n---\n".Length;
        string frontMatter = normalized[..bodyStart].TrimEnd();
        string body = normalized[bodyStart..];

        return new MarkdownParts(frontMatter, body);
    }

    private static string ReattachFrontMatter(string? frontMatter, string body)
    {
        if (string.IsNullOrWhiteSpace(frontMatter))
        {
            return body;
        }

        return frontMatter + "\n\n" + body.TrimStart();
    }

    private static string RenderNormalizedMarkdown(MarkdownDocument document, MarkdownPipeline pipeline)
    {
        // NormalizeRenderer writes Markdown, not HTML. These options keep block spacing stable
        // and standardize unordered lists while leaving Markdig's built-in list indentation logic
        // in charge of nesting and continuation indentation.
        var options = new NormalizeOptions
        {
            SpaceAfterQuoteBlock = true,
            EmptyLineAfterCodeBlock = true,
            EmptyLineAfterHeading = true,
            EmptyLineAfterThematicBreak = true,
            ListItemCharacter = '-',
            ExpandAutoLinks = true
        };

        using var writer = new StringWriter();
        var renderer = new NormalizeRenderer(writer, options);

        // Extensions can register additional normalize renderers, so the renderer must be wired
        // through the same pipeline that parsed the document before it renders the modified AST.
        pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();

        return writer.ToString();
    }

    private static void NormalizeHeadingsToAtx(MarkdownDocument document)
    {
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            // Preserve the node and its inline content; only change rendering metadata.
            // This converts Setext headings into ATX headings when NormalizeRenderer writes them.
            heading.IsSetext = false;
            heading.HeaderChar = '#';
            heading.HeaderCharCount = Math.Clamp(heading.Level, 1, 6);
        }
    }

    private static IReadOnlyList<HeadingChange> NormalizeHeadingHierarchy(MarkdownDocument document)
    {
        var hierarchy = new Stack<(int OriginalLevel, int NormalizedLevel)>();
        var changes = new List<HeadingChange>();

        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            // Preserve the node and its inline content; only change rendering metadata and level.
            // This converts Setext headings into ATX headings when NormalizeRenderer writes them.
            heading.IsSetext = false;
            heading.HeaderChar = '#';

            // Markdig already parses Markdown heading levels in the 1..6 range, but clamping keeps
            // this pass defensive if a future parser extension or manual AST mutation changes that.
            int requestedLevel = Math.Clamp(heading.Level, 1, 6);

            // Preserve sibling and ancestor relationships by comparing against original levels.
            // Equal original levels pop together, so "#", "###", "###" reflows to
            // "#", "##", "##" instead of accidentally making the second "###" a child.
            while (hierarchy.Count > 0 && hierarchy.Peek().OriginalLevel >= requestedLevel)
            {
                hierarchy.Pop();
            }

            // A heading can only be one level deeper than its nearest lower-level ancestor. If
            // there is no such ancestor, promote it to H1 as the document's current root heading.
            int normalizedLevel = hierarchy.Count == 0
                ? 1
                : Math.Min(hierarchy.Peek().NormalizedLevel + 1, 6);

            heading.Level = normalizedLevel;

            // For ATX headings this count is not the primary signal, but keeping it aligned with
            // Level prevents stale Setext underline counts from surviving on a mutated node.
            heading.HeaderCharCount = normalizedLevel;

            hierarchy.Push((requestedLevel, normalizedLevel));
            changes.Add(new HeadingChange(requestedLevel, normalizedLevel, heading.Line + 1));
        }

        return changes;
    }

    private static void RemoveSyntheticHeadingReferenceDefinitions(MarkdownDocument document)
    {
        // UseAdvancedExtensions enables AutoIdentifiers, which injects HeadingLinkReferenceDefinition
        // nodes for heading references. NormalizeRenderer can serialize those implementation details
        // as empty reference definitions, so remove only the synthetic heading definitions and leave
        // any user-authored LinkReferenceDefinition entries intact.
        foreach (LinkReferenceDefinitionGroup group in document.Descendants<LinkReferenceDefinitionGroup>().ToArray())
        {
            foreach (HeadingLinkReferenceDefinition definition in group.OfType<HeadingLinkReferenceDefinition>().ToArray())
            {
                group.Remove(definition);
            }

            if (group.Count == 0)
            {
                group.Parent?.Remove(group);
            }
        }
    }

    private static IReadOnlyList<string> BuildReflowWarnings(
        string rawMarkdown,
        IReadOnlyList<HeadingChange> headingChanges)
    {
        var warnings = new List<string>();

        int changedCount = headingChanges.Count(change => change.OriginalLevel != change.NormalizedLevel);

        if (headingChanges.Count == 0)
        {
            warnings.Add("No Markdown headings were found.");
        }
        else if (changedCount > 0)
        {
            warnings.Add("Review hand-written tables of contents, outline prose, and links that describe heading levels.");
        }

        if (Regex.IsMatch(rawMarkdown, @"<h[1-6]\b", RegexOptions.IgnoreCase))
        {
            warnings.Add("Raw HTML heading tags were detected; reflow does not rewrite HTML headings.");
        }

        if (Regex.IsMatch(rawMarkdown, @"\]\(\s*#|href\s*=\s*[""']#", RegexOptions.IgnoreCase))
        {
            warnings.Add("Anchor links were detected; check any manually maintained navigation after reflow.");
        }

        if (headingChanges.Count > 1 &&
            headingChanges[0].OriginalLevel == 1 &&
            headingChanges[1].OriginalLevel == 2)
        {
            warnings.Add("The first H1 was left in place; remove it manually if it is only the document title.");
        }

        return warnings;
    }

    private sealed record HeadingChange(int OriginalLevel, int NormalizedLevel, int LineNumber);
}
