using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PilahDok.Testing;

var output = args.Length > 0 ? args[0] : "tests/fixtures/sample-structural.docx";
if (args.Contains("--no-headings", StringComparer.Ordinal)) FixtureDocumentBuilder.CreateWithoutHeadings(output);
else FixtureDocumentBuilder.Create(output);
using var document = WordprocessingDocument.Open(output, false);
var validationErrors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(document).ToArray();
if (validationErrors.Length > 0)
{
    foreach (var error in validationErrors.Take(10))
    {
        Console.Error.WriteLine($"{error.Id}: {error.Description}");
    }

    throw new InvalidDataException($"Generated fixture has {validationErrors.Length} Open XML validation error(s).");
}

Console.WriteLine($"Fixture created: {Path.GetFullPath(output)}");
