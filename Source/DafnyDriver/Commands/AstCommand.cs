#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.BaseTypes;
using Microsoft.Dafny;
using Type = System.Type;

namespace DafnyDriver.Commands;

public static class AstCommand {
  public static readonly Option<FileInfo?> OutputOption = new(["--output", "-o"],
    "Write the dumped AST to this file. Defaults to stdout.") {
    ArgumentHelpName = "file"
  };

  public static readonly Option<bool> IncludeLocationsOption = new("--include-locations",
    "Include source locations in the dumped AST.");

  public static readonly Option<bool> IncludeFilePathsOption = new("--include-file-paths",
    "Include file paths in the dumped AST.");

  static AstCommand() {
    OptionRegistry.RegisterOption(OutputOption, OptionScope.Cli);
    OptionRegistry.RegisterOption(IncludeLocationsOption, OptionScope.Cli);
    OptionRegistry.RegisterOption(IncludeFilePathsOption, OptionScope.Cli);
  }

  public static Command Create() {
    var result = new Command("ast", "Parse Dafny sources and dump a machine-readable AST as JSON.");
    result.AddArgument(DafnyCommands.FilesArgument);
    result.AddOption(OutputOption);
    result.AddOption(IncludeLocationsOption);
    result.AddOption(IncludeFilePathsOption);
    foreach (var option in DafnyCommands.ParserOptions.Concat(DafnyCommands.ConsoleOutputOptions)) {
      result.AddOption(option);
    }

    DafnyNewCli.SetHandlerUsingDafnyOptionsContinuation(result, async (options, _) => {
      var cliCompilation = CliCompilation.Create(options);
      cliCompilation.Compilation.ShouldProcessSolverOptions = false;
      cliCompilation.Start();

      try {
        var program = await cliCompilation.Compilation.ParsedProgram;
        if (program != null) {
          var astOptions = new AstDumpOptions(
            options.Get(IncludeLocationsOption),
            options.Get(IncludeFilePathsOption));
          var serialized = SerializeParsedProgram(program, astOptions);
          var outputPath = options.Get(OutputOption)?.FullName;
          await using var writer = outputPath == null
            ? options.OutputWriter.StatusWriter()
            : new StreamWriter(outputPath);
          await writer.WriteLineAsync(serialized);
        }
      } finally {
        cliCompilation.Compilation.Dispose();
      }

      return await cliCompilation.GetAndReportExitCode();
    });

    return result;
  }

  public static string SerializeParsedProgram(Program program, AstDumpOptions options) {
    var files = program.Files.Select(file => new FileHeader(
      file.Origin.Uri?.LocalPath ?? "",
      file.Origin.Uri != null && program.Compilation.AlreadyVerifiedRoots.Contains(file.Origin.Uri),
      file.TopLevelDecls.ToList())).ToList();
    var root = new FilesContainer(files);

    var serializer = new AstJsonSerializer(options);
    var json = serializer.Serialize(root);
    if (json == null) {
      return "null";
    }
    return json.ToJsonString(new JsonSerializerOptions {
      WriteIndented = true
    });
  }

  public readonly record struct AstDumpOptions(
    bool IncludeLocations,
    bool IncludeFilePaths);

  private sealed class AstJsonSerializer(AstDumpOptions options) {
    private readonly HashSet<object> activeObjects = new(ReferenceEqualityComparer.Instance);

    public JsonNode? Serialize(object? value) {
      return SerializeValue(value);
    }

    private JsonNode? SerializeValue(object? value) {
      if (value == null) {
        return null;
      }

      var actualType = value.GetType();

      if (value is string s) {
        return JsonValue.Create(s);
      }

      if (value is bool b) {
        return JsonValue.Create(b);
      }

      if (value is char c) {
        return JsonValue.Create(c.ToString());
      }

      if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal) {
        return JsonValue.Create(value.ToString());
      }

      if (value is BigInteger || value is BigDec) {
        return JsonValue.Create(value.ToString());
      }

      if (value is Enum enumValue) {
        return JsonValue.Create(enumValue.ToString());
      }

      if (value is Uri uri) {
        return options.IncludeFilePaths ? JsonValue.Create(uri.LocalPath) : null;
      }

      if (value is IOrigin origin) {
        return options.IncludeLocations ? SerializeOrigin(origin) : null;
      }

      if (value is Token token) {
        return options.IncludeLocations ? SerializeToken(token) : null;
      }

      if (value is TokenRange tokenRange) {
        return options.IncludeLocations ? SerializeTokenRange(tokenRange) : null;
      }

      if (value is IDictionary dictionary) {
        return SerializeDictionary(dictionary);
      }

      if (value is IEnumerable enumerable) {
        return SerializeEnumerable(enumerable);
      }

      return SerializeObject(value, actualType);
    }

    private JsonObject SerializeOrigin(IOrigin origin) {
      return new JsonObject {
        ["$kind"] = origin.GetType().Name,
        ["reportingRange"] = SerializeTokenRange(origin.ReportingRange)
      };
    }

    private JsonObject SerializeToken(Token token) {
      return new JsonObject {
        ["line"] = token.line,
        ["col"] = token.col,
        ["pos"] = token.pos,
        ["uri"] = options.IncludeFilePaths ? token.Uri?.LocalPath : null
      };
    }

    private JsonObject SerializeTokenRange(TokenRange range) {
      return new JsonObject {
        ["start"] = SerializeToken(range.StartToken),
        ["end"] = range.EndToken == null ? null : SerializeToken(range.EndToken)
      };
    }

    private JsonObject SerializeDictionary(IDictionary dictionary) {
      var result = new JsonObject();
      foreach (DictionaryEntry entry in dictionary) {
        result[entry.Key?.ToString() ?? "<null>"] = SerializeValue(entry.Value);
      }
      return result;
    }

    private JsonArray SerializeEnumerable(IEnumerable enumerable) {
      var result = new JsonArray();
      foreach (var element in enumerable) {
        result.Add(SerializeValue(element));
      }
      return result;
    }

    private JsonObject SerializeObject(object value, Type actualType) {
      if (!activeObjects.Add(value)) {
        return new JsonObject {
          ["$kind"] = actualType.Name,
          ["$cycle"] = true
        };
      }

      try {
        var result = new JsonObject {
          ["$kind"] = actualType.Name
        };

        if (TryGetParseConstructor(actualType, out var constructor)) {
          foreach (var parameter in constructor.GetParameters()) {
            if (parameter.GetCustomAttribute<BackEdge>() != null) {
              continue;
            }

            if (!options.IncludeLocations && IsLocationType(parameter.ParameterType)) {
              continue;
            }

            var member = FindMember(actualType, parameter.Name!);
            if (member == null || member.GetCustomAttribute<BackEdge>() != null) {
              continue;
            }

            if (!options.IncludeFilePaths && member.Name.Equals("Uri", StringComparison.OrdinalIgnoreCase)) {
              continue;
            }

            var memberValue = GetValue(member, value);
            var serializedValue = SerializeValue(memberValue);
            if (serializedValue != null) {
              result[parameter.Name!] = serializedValue;
            }
          }
          return result;
        }

        foreach (var property in actualType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
          if (!property.CanRead || property.GetIndexParameters().Length != 0) {
            continue;
          }

          if (!options.IncludeLocations && IsLocationType(property.PropertyType)) {
            continue;
          }

          if (!options.IncludeFilePaths && property.Name.Equals("Uri", StringComparison.OrdinalIgnoreCase)) {
            continue;
          }

          var propertyValue = property.GetValue(value);
          var serializedValue = SerializeValue(propertyValue);
          if (serializedValue != null) {
            result[property.Name] = serializedValue;
          }
        }

        return result;
      } finally {
        activeObjects.Remove(value);
      }
    }

    private static bool IsLocationType(Type type) {
      var targetType = Nullable.GetUnderlyingType(type) ?? type;
      return typeof(IOrigin).IsAssignableFrom(targetType) ||
             targetType == typeof(Token) ||
             targetType == typeof(TokenRange);
    }

    private static object? GetValue(MemberInfo member, object instance) {
      return member switch {
        PropertyInfo property => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => throw new InvalidOperationException($"Unsupported member type {member.MemberType}")
      };
    }

    private static MemberInfo? FindMember(Type type, string name) {
      for (var current = type; current != null; current = current.BaseType) {
        var property = current.GetProperties(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
          .FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property != null) {
          return property;
        }

        var field = current.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
          .FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (field != null) {
          return field;
        }
      }

      return null;
    }

    private static bool TryGetParseConstructor(Type type, out ConstructorInfo constructor) {
      constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .Where(candidate => !candidate.IsPrivate &&
                            !candidate.GetParameters().Any(parameter => typeof(Cloner).IsAssignableFrom(parameter.ParameterType)))
        .OrderByDescending(candidate => candidate.GetCustomAttribute<SyntaxConstructorAttribute>() != null)
        .ThenByDescending(candidate => candidate.GetParameters().Length)
        .FirstOrDefault()!;
      return constructor != null;
    }
  }
}
