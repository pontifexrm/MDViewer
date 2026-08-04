# Quarterly Operations Review

A short document that exercises the renderer: **bold**, *italic*, `inline code`,
and a [link](https://example.com).

## Findings

1. First ordered item
2. Second ordered item
   - Nested bullet
   - Another nested bullet

> A block quote, to check the left rule and spacing.

## Metrics

| Region | Q1 | Q2 | Change |
|---|---:|---:|---:|
| Auckland | 1,240 | 1,455 | +17.3% |
| Wellington | 880 | 812 | −7.7% |
| Christchurch | 605 | 690 | +14.0% |

## Code

```csharp
public static string ToHtmlString(string? markdown) =>
    Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
```

## Task list

- [x] Render markdown
- [x] Print and PDF
- [ ] Ship it

---

Footnote reference here.[^1]

[^1]: And the footnote text, which needs the advanced extensions to render.
