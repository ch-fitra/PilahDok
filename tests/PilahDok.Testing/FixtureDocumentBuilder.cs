using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PilahDok.Testing;

public static class FixtureDocumentBuilder
{
    public static void CreateWithoutHeadings(string path)
    {
        Create(path);
        using var document = WordprocessingDocument.Open(path, true);
        foreach (var paragraph in document.MainDocumentPart!.Document!.Body!.Descendants<W.Paragraph>())
        {
            if (paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value is "Heading1" or "Heading2")
            {
                paragraph.ParagraphProperties.ParagraphStyleId.Remove();
                foreach (var text in paragraph.Descendants<W.Text>()) text.Text = "Catatan kegiatan dan informasi lanjutan.";
            }
        }
        document.MainDocumentPart.Document.Save();
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        AddStyles(main);
        AddNumbering(main);

        var header = main.AddNewPart<HeaderPart>();
        header.Header = new W.Header(Paragraph("Fixture PilahDok — Header"));
        var headerId = main.GetIdOfPart(header);
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new W.Footer(Paragraph("Fixture PilahDok — Footer"));
        var footerId = main.GetIdOfPart(footer);

        var imagePart = main.AddImagePart(ImagePartType.Png);
        using (var imageStream = new MemoryStream(TinyPng, writable: false))
        {
            imagePart.FeedData(imageStream);
        }

        var imageId = main.GetIdOfPart(imagePart);
        var hyperlinkRelationship = main.AddHyperlinkRelationship(new Uri("https://example.com/fixture"), true);
        var body = main.Document.Body!;
        body.Append(
            Paragraph("Bagian awal sebelum heading."),
            StyledParagraph("BAB I PENDAHULUAN", "Heading1"),
            Paragraph("Isi pendahuluan dengan format tebal.", bold: true),
            NumberedParagraph("Butir bernomor pertama."),
            new W.Paragraph(new W.Run(CreateImage(imageId))),
            StyledParagraph("Latar Belakang", "Heading2"),
            CreateTable(),
            new W.Paragraph(
                new W.Run(
                    new W.Text("Akhir halaman eksplisit."),
                    new W.Break { Type = W.BreakValues.Page },
                    new W.Text("Teks ini tetap berada dalam paragraf yang sama."))),
            StyledParagraph("BAB II TINJAUAN PUSTAKA", "Heading1"),
            new W.Paragraph(
                new W.Hyperlink(new W.Run(new W.RunProperties(new W.Color { Val = "0563C1" }), new W.Text("Tautan fixture")))
                {
                    Id = hyperlinkRelationship.Id,
                    History = OnOffValue.FromBoolean(true)
                }),
            new W.Paragraph(
                new W.ParagraphProperties(CreateSectionProperties(headerId, footerId)),
                new W.Run(new W.Text("Batas section setelah paragraf ini."))),
            StyledParagraph("BAB III METODOLOGI", "Heading1"),
            Paragraph("Isi metodologi."),
            CreateSectionProperties(headerId, footerId));
        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        part.Styles = new W.Styles(
            new W.Style(new W.StyleName { Val = "Normal" })
            {
                Type = W.StyleValues.Paragraph,
                StyleId = "Normal",
                Default = OnOffValue.FromBoolean(true)
            },
            HeadingStyle("Heading1", "Heading 1", 0, "32"),
            HeadingStyle("Heading2", "Heading 2", 1, "26"));
        part.Styles.Save();
    }

    private static W.Style HeadingStyle(string id, string name, int outlineLevel, string fontSize)
    {
        return new W.Style(
            new W.StyleName { Val = name },
            new W.BasedOn { Val = "Normal" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.UIPriority { Val = outlineLevel + 1 },
            new W.PrimaryStyle(),
            new W.StyleParagraphProperties(
                new W.KeepNext(),
                new W.KeepLines(),
                new W.SpacingBetweenLines { Before = "240", After = "120" },
                new W.OutlineLevel { Val = outlineLevel }),
            new W.StyleRunProperties(new W.Bold(), new W.FontSize { Val = fontSize }))
        {
            Type = W.StyleValues.Paragraph,
            StyleId = id
        };
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new W.Numbering(
            new W.AbstractNum(
                new W.MultiLevelType { Val = W.MultiLevelValues.SingleLevel },
                new W.Level(
                    new W.StartNumberingValue { Val = 1 },
                    new W.NumberingFormat { Val = W.NumberFormatValues.Decimal },
                    new W.LevelText { Val = "%1." },
                    new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                    new W.PreviousParagraphProperties(
                        new W.Indentation { Left = "720", Hanging = "360" }))
                {
                    LevelIndex = 0
                })
            {
                AbstractNumberId = 0
            },
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = 1 });
        part.Numbering.Save();
    }

    private static W.Paragraph NumberedParagraph(string text)
    {
        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.NumberingProperties(
                    new W.NumberingLevelReference { Val = 0 },
                    new W.NumberingId { Val = 1 })),
            new W.Run(new W.Text(text)));
    }

    private static W.Paragraph StyledParagraph(string text, string styleId)
    {
        return new W.Paragraph(
            new W.ParagraphProperties(new W.ParagraphStyleId { Val = styleId }),
            new W.Run(new W.Text(text)));
    }

    private static W.Paragraph Paragraph(string text, bool bold = false)
    {
        var run = new W.Run(new W.Text(text));
        if (bold)
        {
            run.PrependChild(new W.RunProperties(new W.Bold()));
        }

        return new W.Paragraph(run);
    }

    private static W.Table CreateTable()
    {
        return new W.Table(
            new W.TableProperties(
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })),
            new W.TableGrid(
                new W.GridColumn { Width = "4500" },
                new W.GridColumn { Width = "4500" }),
            new W.TableRow(
                new W.TableCell(Paragraph("Kolom A")),
                new W.TableCell(Paragraph("Kolom B"))),
            new W.TableRow(
                new W.TableCell(Paragraph("Nilai 1")),
                new W.TableCell(Paragraph("Nilai 2"))));
    }

    private static W.Drawing CreateImage(string relationshipId)
    {
        const long width = 914_400L;
        const long height = 914_400L;
        return new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = width, Cy = height },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = 1U, Name = "Gambar fixture" },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = "fixture.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0, Y = 0 },
                                    new A.Extents { Cx = width, Cy = height }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle
                                })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture"
                    }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U
            });
    }

    private static W.SectionProperties CreateSectionProperties(string headerId, string footerId)
    {
        return new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = headerId },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = footerId },
            new W.PageSize { Width = 11_906U, Height = 16_838U },
            new W.PageMargin
            {
                Top = 1_440,
                Right = 1_440U,
                Bottom = 1_440,
                Left = 1_440U,
                Header = 720U,
                Footer = 720U,
                Gutter = 0U
            });
    }
}
