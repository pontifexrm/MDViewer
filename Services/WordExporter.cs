using System.Text;

namespace MDViewer.Services;

/// <summary>
/// Writes a Word-openable .doc: HTML wrapped in the Office document header Word
/// recognises. Not a real binary .doc or OOXML .docx — Word (and LibreOffice) open
/// it, honour the styling, and will re-save it as .docx on request. Chosen over
/// OpenXml because it needs no dependency and keeps the exact same HTML the
/// on-screen render and the ePub use, so all three outputs stay consistent.
/// </summary>
public static class WordExporter
{
    /// <param name="includeTitleHeading">
    /// False when the document already opens with its own H1, so Word doesn't show
    /// the title twice.
    /// </param>
    public static byte[] ToDoc(string title, string bodyHtml, bool includeTitleHeading = true)
    {
        var html = $$"""
            <html xmlns:o="urn:schemas-microsoft-com:office:office"
                  xmlns:w="urn:schemas-microsoft-com:office:word"
                  xmlns="http://www.w3.org/TR/REC-html40">
            <head>
            <meta charset="utf-8" />
            <title>{{Html(title)}}</title>
            <!--[if gte mso 9]><xml>
              <w:WordDocument>
                <w:View>Print</w:View>
                <w:Zoom>100</w:Zoom>
              </w:WordDocument>
            </xml><![endif]-->
            <style>
            @page { size: A4; margin: 2cm; }
            body { font-family: Calibri, Arial, sans-serif; font-size: 11pt; line-height: 1.5; color: #111; }
            h1, h2, h3, h4, h5, h6 { font-family: 'Calibri Light', Arial, sans-serif; color: #1f3864; line-height: 1.25; }
            h1 { font-size: 20pt; } h2 { font-size: 16pt; } h3 { font-size: 13pt; } h4 { font-size: 11.5pt; }
            p { margin: 0 0 8pt 0; }
            ul, ol { margin: 0 0 8pt 0; }
            li { margin: 0 0 3pt 0; }
            table { border-collapse: collapse; width: 100%; margin: 8pt 0; font-size: 10pt; }
            th, td { border: 1px solid #b0b0b0; padding: 4pt 6pt; text-align: left; vertical-align: top; }
            th { background: #eef1f6; font-weight: bold; }
            code { font-family: Consolas, 'Courier New', monospace; font-size: 10pt; background: #f4f4f4; }
            pre { font-family: Consolas, 'Courier New', monospace; font-size: 9.5pt; background: #f4f4f4;
                  border: 1px solid #ddd; padding: 6pt; }
            blockquote { margin: 8pt 0 8pt 18pt; padding-left: 10pt; border-left: 3pt solid #ccc; color: #555; }
            img { max-width: 100%; }
            a { color: #0563c1; }
            </style>
            </head>
            <body>
            {{(includeTitleHeading ? $"<h1>{Html(title)}</h1>" : "")}}
            {{bodyHtml}}
            </body>
            </html>
            """;

        // Word reads the encoding from the BOM more reliably than from the meta tag.
        // GetBytes never emits the preamble, so it has to be prepended by hand.
        var encoding = new UTF8Encoding(true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(html)];
    }

    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
