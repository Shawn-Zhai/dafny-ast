# dafny-ast

This fork of `dafny-lang/dafny` adds an `ast` command that parses Dafny source
files and dumps a machine-readable JSON representation of the parsed AST.

## Usage

After building the local driver, run the AST command through the wrapper:

```bash
./Scripts/dafny_ast ast path/to/file.dfy
```

Write the JSON dump to a file:

```bash
./Scripts/dafny_ast ast path/to/file.dfy -o ast.json
```

Include source locations and file paths:

```bash
./Scripts/dafny_ast ast path/to/file.dfy \
  --include-locations \
  --include-file-paths \
  -o ast.json
```

## Build And Test

After a fresh clone, build the local driver before running `Scripts/dafny_ast`. 
The wrapper does not download or build Dafny for you; 
it only launches the driver produced by this repository.

Install the .NET SDK version expected by `global.json`, then build the local
driver:

```bash
dotnet build Source/DafnyDriver/DafnyDriver.csproj \
  --disable-build-servers \
  -maxcpucount:1
```

`dotnet build` restores the required packages and local tools automatically.

The wrapper expects the local driver at:

```text
Binaries/net8.0/DafnyDriver
```

or:

```text
Binaries/net8.0/DafnyDriver.dll
```

Run the AST command test after building:

```bash
dotnet test Source/DafnyDriver.Test/DafnyDriver.Test.csproj \
  --filter AstCommandTest \
  --disable-build-servers \
  -maxcpucount:1
```

Then check the command help:

```bash
./Scripts/dafny_ast ast --help
```

## AST Output

The JSON output is intended for tooling that needs a structured view of Dafny
source syntax.

By default, the dump omits source locations and file paths to keep output more
stable across machines. Use these flags when that information is needed:

- `--include-locations`: include token ranges and source positions.
- `--include-file-paths`: include file paths in URI fields.
- `-o, --output <file>`: write JSON to a file instead of stdout.

Serialized AST objects include a `$kind` field with the underlying Dafny AST
node type, such as `Method` or `AssertStmt`.

## License

This fork follows the upstream Dafny license. See `LICENSE.txt`.
