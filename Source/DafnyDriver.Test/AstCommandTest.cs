using System.IO;
using DafnyDriver.Commands;
using Microsoft.Dafny;

namespace DafnyDriver.Test;

public class AstCommandTest {
  [Fact]
  public async Task ParsedAstDumpOmitsLocationsAndKeepsNamedFields() {
    const string source = """
method M(x: int)
  requires x > 0
  ensures x == 1
{
  assert x > 0;
}
""";

    var uri = new Uri(Path.Combine(Path.GetTempPath(), "AstCommandTest.dfy"));
    var reporter = new BatchErrorReporter(DafnyOptions.Default);
    var parseResult = await ProgramParser.Parse(source, uri, reporter);

    Assert.False(reporter.HasErrors);

    var astJson = AstCommand.SerializeParsedProgram(parseResult.Program,
      new AstCommand.AstDumpOptions(IncludeLocations: false, IncludeFilePaths: false));

    Assert.Contains("\"$kind\": \"Method\"", astJson);
    Assert.Contains("\"req\"", astJson);
    Assert.Contains("\"ens\"", astJson);
    Assert.Contains("\"body\"", astJson);
    Assert.Contains("\"$kind\": \"AssertStmt\"", astJson);
    Assert.DoesNotContain("\"origin\"", astJson);
    Assert.DoesNotContain(uri.LocalPath, astJson);
  }
}
